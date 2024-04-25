﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.ContentStorage;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.FileSystem.DevTools;
using Microsoft.KernelMemory.MemoryStorage;
using Microsoft.KernelMemory.Pipeline;

namespace Microsoft.KernelMemory.Handlers;

public class SaveRecordsHandler : IPipelineStepHandler
{
    private class FileDetailsWithRecordId
    {
        public string RecordId { get; set; }

        public DataPipeline.GeneratedFileDetails File { get; set; }

        public FileDetailsWithRecordId(DataPipeline pipeline, DataPipeline.GeneratedFileDetails file)
        {
            this.File = file;
            this.RecordId = GetRecordId(pipeline.DocumentId, file.Id);
        }

        private static string GetRecordId(string documentId, string partId)
        {
            // Note: this value is serialized in different ways depending on the memory DB,
            // don't use it for search, and use tags instead e.g.
            //  Filtering by document ID:
            //  - $filter=tags/any(s: s eq '__document_id:doc001')&$select=tags,payload
            //  Filtering by file ID (a document can contain multiple files):
            //  - $filter=tags/any(s: s eq '__file_id:bfd793c29f1642c2b085a11ccc38f27d')&$select=tags,payload
            //  Filtering by chunk/partition ID:
            //  - $filter=tags/any(s: s eq '__file_part:6479a127dbcc38f3c085b1c2c29f1fd2')&$select=tags,payload
            return $"d={documentId}//p={partId}";
        }
    }

    private readonly IPipelineOrchestrator _orchestrator;
    private readonly List<IMemoryDb> _memoryDbs;
    private readonly ILogger<SaveRecordsHandler> _log;
    private readonly bool _embeddingGenerationEnabled;

    /// <inheritdoc />
    public string StepName { get; }

    /// <summary>
    /// Handler responsible for copying embeddings from storage to list of memory DBs
    /// Note: stepName and other params are injected with DI
    /// </summary>
    /// <param name="stepName">Pipeline step for which the handler will be invoked</param>
    /// <param name="orchestrator">Current orchestrator used by the pipeline, giving access to content and other helps.</param>
    /// <param name="log">Application logger</param>
    public SaveRecordsHandler(
        string stepName,
        IPipelineOrchestrator orchestrator,
        ILogger<SaveRecordsHandler>? log = null)
    {
        this.StepName = stepName;
        this._log = log ?? DefaultLogger<SaveRecordsHandler>.Instance;
        this._embeddingGenerationEnabled = orchestrator.EmbeddingGenerationEnabled;

        this._orchestrator = orchestrator;
        this._memoryDbs = orchestrator.GetMemoryDbs();

        if (this._memoryDbs.Count < 1)
        {
            this._log.LogError("Handler {0} NOT ready, no memory DB configured", stepName);
        }
        else
        {
            this._log.LogInformation("Handler {0} ready, {1} vector storages", stepName, this._memoryDbs.Count);
        }
    }

    /// <inheritdoc />
    public async Task<(bool success, DataPipeline updatedPipeline)> InvokeAsync(
        DataPipeline pipeline, CancellationToken cancellationToken = default)
    {
        this._log.LogDebug("Saving memory records, pipeline '{0}/{1}'", pipeline.Index, pipeline.DocumentId);

        await this.DeletePreviousRecordsAsync(pipeline, cancellationToken).ConfigureAwait(false);
        pipeline.PreviousExecutionsToPurge = new List<DataPipeline>();

        return this._embeddingGenerationEnabled
            ? await this.SaveEmbeddingsAsync(pipeline, cancellationToken).ConfigureAwait(false)
            : await this.SavePartitionsAsync(pipeline, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loop through all the EMBEDDINGS generated, creating a memory record for each one
    /// </summary>
    public async Task<(bool success, DataPipeline updatedPipeline)> SaveEmbeddingsAsync(
        DataPipeline pipeline, CancellationToken cancellationToken = default)
    {
        var embeddingsFound = false;

        // TODO: replace with ConditionalWeakTable indexing on this._memoryDbs
        var createdIndexes = new HashSet<string>();

        // For each embedding file => For each Memory DB => Upsert record
        foreach (FileDetailsWithRecordId embeddingFile in GetListOfEmbeddingFiles(pipeline))
        {
            if (embeddingFile.File.AlreadyProcessedBy(this))
            {
                embeddingsFound = true;
                this._log.LogTrace("File {0} already processed by this handler", embeddingFile.File.Name);
                continue;
            }

            string vectorJson = await this._orchestrator.ReadTextFileAsync(pipeline, embeddingFile.File.Name, cancellationToken).ConfigureAwait(false);
            EmbeddingFileContent? embeddingData = JsonSerializer.Deserialize<EmbeddingFileContent>(vectorJson.RemoveBOM().Trim());
            if (embeddingData == null)
            {
                throw new OrchestrationException($"Unable to deserialize embedding file {embeddingFile.File.Name}");
            }

            embeddingsFound = true;

            DataPipeline.FileDetails fileDetails = pipeline.GetFile(embeddingFile.File.ParentId);
            string partitionContent = await this._orchestrator.ReadTextFileAsync(pipeline, embeddingData.SourceFileName, cancellationToken).ConfigureAwait(false);
            string url = await this.GetSourceUrlAsync(pipeline, fileDetails, cancellationToken).ConfigureAwait(false);

            var record = PrepareRecord(
                pipeline: pipeline,
                recordId: embeddingFile.RecordId,
                fileName: fileDetails.Name,
                url: url,
                fileId: embeddingFile.File.ParentId,
                partitionFileId: embeddingFile.File.SourcePartitionId,
                partitionContent: partitionContent,
                partitionNumber: embeddingFile.File.PartitionNumber,
                sectionNumber: embeddingFile.File.SectionNumber,
                partitionEmbedding: embeddingData.Vector,
                embeddingGeneratorProvider: embeddingData.GeneratorProvider,
                embeddingGeneratorName: embeddingData.GeneratorName,
                embeddingFile.File.Tags);

            foreach (IMemoryDb client in this._memoryDbs)
            {
                try
                {
                    await this.CreateIndexOnceAsync(client, createdIndexes, pipeline.Index, record.Vector.Length, cancellationToken).ConfigureAwait(false);

                    this._log.LogTrace("Saving record {0} in index '{1}'", record.Id, pipeline.Index);
                    await client.UpsertAsync(pipeline.Index, record, cancellationToken).ConfigureAwait(false);
                }
                catch (IndexNotFound e)
                {
                    this._log.LogWarning(e, "Index {0} not found, attempting to create it", pipeline.Index);
                    await this.CreateIndexOnceAsync(client, createdIndexes, pipeline.Index, record.Vector.Length, cancellationToken, true).ConfigureAwait(false);

                    this._log.LogTrace("Retry: Saving record {0} in index '{1}'", record.Id, pipeline.Index);
                    await client.UpsertAsync(pipeline.Index, record, cancellationToken).ConfigureAwait(false);
                }
            }

            embeddingFile.File.MarkProcessedBy(this);
        }

        if (!embeddingsFound)
        {
            this._log.LogWarning("Pipeline '{0}/{1}': embeddings not found, cannot save embeddings, moving to next pipeline step.", pipeline.Index, pipeline.DocumentId);
        }

        return (true, pipeline);
    }

    /// <summary>
    /// Loop through all the PARTITIONS and SYNTHETIC chunks, creating a memory record for each one
    /// </summary>
    public async Task<(bool success, DataPipeline updatedPipeline)> SavePartitionsAsync(
        DataPipeline pipeline, CancellationToken cancellationToken = default)
    {
        var partitionsFound = false;

        // TODO: replace with ConditionalWeakTable indexing on this._memoryDbs
        var createdIndexes = new HashSet<string>();

        // Create records only for partitions (text chunks) and synthetic data
        foreach (FileDetailsWithRecordId file in GetListOfPartitionAndSyntheticFiles(pipeline))
        {
            if (file.File.AlreadyProcessedBy(this))
            {
                partitionsFound = true;
                this._log.LogTrace("File {0} already processed by this handler", file.File.Name);
                continue;
            }

            partitionsFound = true;

            switch (file.File.MimeType)
            {
                case MimeTypes.PlainText:
                case MimeTypes.MarkDown:

                    DataPipeline.FileDetails partitionFileDetails = pipeline.GetFile(file.File.ParentId);
                    string partitionContent = await this._orchestrator.ReadTextFileAsync(pipeline, file.File.Name, cancellationToken).ConfigureAwait(false);
                    string url = await this.GetSourceUrlAsync(pipeline, partitionFileDetails, cancellationToken).ConfigureAwait(false);

                    var record = PrepareRecord(
                        pipeline: pipeline,
                        recordId: file.RecordId,
                        fileName: partitionFileDetails.Name,
                        url: url,
                        fileId: file.File.ParentId,
                        partitionFileId: file.File.Id,
                        partitionContent: partitionContent,
                        partitionNumber: partitionFileDetails.PartitionNumber,
                        sectionNumber: partitionFileDetails.SectionNumber,
                        partitionEmbedding: new Embedding(),
                        embeddingGeneratorProvider: "",
                        embeddingGeneratorName: "",
                        file.File.Tags);

                    foreach (IMemoryDb client in this._memoryDbs)
                    {
                        try
                        {
                            await this.CreateIndexOnceAsync(client, createdIndexes, pipeline.Index, record.Vector.Length, cancellationToken).ConfigureAwait(false);

                            this._log.LogTrace("Saving record {0} in index '{1}'", record.Id, pipeline.Index);
                            await client.UpsertAsync(pipeline.Index, record, cancellationToken).ConfigureAwait(false);
                        }
                        catch (IndexNotFound e)
                        {
                            this._log.LogWarning(e, "Index {0} not found, attempting to create it", pipeline.Index);
                            await this.CreateIndexOnceAsync(client, createdIndexes, pipeline.Index, record.Vector.Length, cancellationToken, true).ConfigureAwait(false);

                            this._log.LogTrace("Retry: saving record {0} in index '{1}'", record.Id, pipeline.Index);
                            await client.UpsertAsync(pipeline.Index, record, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    break;

                default:
                    this._log.LogWarning("File {0} cannot be used to generate embedding, type not supported", file.File.Name);
                    continue;
            }

            file.File.MarkProcessedBy(this);
        }

        if (!partitionsFound)
        {
            this._log.LogWarning("Pipeline '{0}/{1}': partitions and synthetic records not found, cannot save, moving to next pipeline step.", pipeline.Index, pipeline.DocumentId);
        }

        return (true, pipeline);
    }

    private async Task DeletePreviousRecordsAsync(DataPipeline pipeline, CancellationToken cancellationToken)
    {
        if (pipeline.PreviousExecutionsToPurge.Count == 0) { return; }

        var recordsToKeep = new HashSet<string>();

        // Decide which records not to delete, looking at the current pipeline
        foreach (FileDetailsWithRecordId embeddingFile in GetListOfEmbeddingFiles(pipeline).Concat(GetListOfPartitionAndSyntheticFiles(pipeline)))
        {
            recordsToKeep.Add(embeddingFile.RecordId);
        }

        // Purge old pipelines data, unless it's still relevant in the current pipeline
        foreach (DataPipeline oldPipeline in pipeline.PreviousExecutionsToPurge)
        {
            foreach (FileDetailsWithRecordId file in GetListOfEmbeddingFiles(oldPipeline).Concat(GetListOfPartitionAndSyntheticFiles(oldPipeline)))
            {
                if (recordsToKeep.Contains(file.RecordId)) { continue; }

                foreach (IMemoryDb client in this._memoryDbs)
                {
                    this._log.LogTrace("Deleting old record {0}", file.RecordId);
                    await client.DeleteAsync(pipeline.Index, new MemoryRecord { Id = file.RecordId }, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static IEnumerable<FileDetailsWithRecordId> GetListOfEmbeddingFiles(DataPipeline pipeline)
    {
        return pipeline.Files.SelectMany(f1 => f1.GeneratedFiles.Where(
                f2 => f2.Value.ArtifactType == DataPipeline.ArtifactTypes.TextEmbeddingVector)
            .Select(x => new FileDetailsWithRecordId(pipeline, x.Value)));
    }

    private static IEnumerable<FileDetailsWithRecordId> GetListOfPartitionAndSyntheticFiles(DataPipeline pipeline)
    {
        return pipeline.Files.SelectMany(f1 => f1.GeneratedFiles.Where(
                f2 => f2.Value.ArtifactType == DataPipeline.ArtifactTypes.TextPartition || f2.Value.ArtifactType == DataPipeline.ArtifactTypes.SyntheticData)
            .Select(x => new FileDetailsWithRecordId(pipeline, x.Value)));
    }

    private async Task CreateIndexOnceAsync(
        IMemoryDb client,
        HashSet<string> createdIndexes,
        string indexName,
        int vectorLength,
        CancellationToken cancellationToken,
        bool force = false)
    {
        // TODO: add support for the same client being used multiple times with different models with the same vectorLength
        var key = $"{client.GetType().Name}::{indexName}::{vectorLength}";

        if (!force && createdIndexes.Contains(key)) { return; }

        this._log.LogTrace("Creating index '{0}'", indexName);
        await client.CreateIndexAsync(indexName, vectorLength, cancellationToken).ConfigureAwait(false);
        createdIndexes.Add(key);
    }

    private async Task<string> GetSourceUrlAsync(
        DataPipeline pipeline,
        DataPipeline.FileDetails file,
        CancellationToken cancellationToken)
    {
        if (file.MimeType != MimeTypes.WebPageUrl)
        {
            return string.Empty;
        }

        BinaryData fileContent = await this._orchestrator.ReadFileAsync(pipeline, file.Name, cancellationToken)
            .ConfigureAwait(false);
        return fileContent.ToString();
    }

    /// <summary>
    /// Prepare a records to be saved in memory DB
    /// </summary>
    /// <param name="pipeline">Pipeline object (TODO: pass only data)</param>
    /// <param name="recordId">DB record ID</param>
    /// <param name="fileName">Filename</param>
    /// <param name="url">Web page URL, if any</param>
    /// <param name="fileId">ID assigned to the file (note: a document can contain multiple files)</param>
    /// <param name="partitionFileId">ID assigned to the partition (or synth) file generated during the import</param>
    /// <param name="partitionContent">Content of the partition</param>
    /// <param name="partitionNumber">Number of the partition, starting from zero</param>
    /// <param name="sectionNumber">Page number (if the doc is paginated), audio segment number, video scene number, etc.</param>
    /// <param name="partitionEmbedding">Embedding vector calculated from the partition content</param>
    /// <param name="embeddingGeneratorProvider">Name of the embedding provider (e.g. Azure), for future use when using multiple embedding types concurrently</param>
    /// <param name="embeddingGeneratorName">Name of the model used to generate embeddings, for future use</param>
    /// <param name="tags">Collection of tags assigned to the record</param>
    /// <returns>Memory record ready to be saved</returns>
    private static MemoryRecord PrepareRecord(
        DataPipeline pipeline,
        string recordId,
        string fileName,
        string url,
        string fileId,
        string partitionFileId,
        string partitionContent,
        int partitionNumber,
        int sectionNumber,
        Embedding partitionEmbedding,
        string embeddingGeneratorProvider,
        string embeddingGeneratorName,
        TagCollection tags)
    {
        var record = new MemoryRecord { Id = recordId };

        /*
         * DOCUMENT DETAILS
         */

        // Document ID provided by the user, e.g. "my-doc-001"
        record.Tags.Add(Constants.ReservedDocumentIdTag, pipeline.DocumentId);

        /*
         * FILE DETAILS
         */

        // File type, e.g. "application/pdf" - Can be used for filtering by file type
        record.Tags.Add(Constants.ReservedFileTypeTag, pipeline.GetFile(fileId).MimeType);

        // File ID assigned by the system, e.g. "f00f0b3116ae423db22ebc80302b129c". New GUID generated by the orchestrator.
        // Can be used for filtering and deletions/purge
        record.Tags.Add(Constants.ReservedFileIdTag, fileId);

        // Original file name, useful for context, e.g. "NASA-August-2043.pdf"
        record.Payload[Constants.ReservedPayloadFileNameField] = fileName;

        // Web page URL, used when importing from a URL (the file name is not useful in that case)
        record.Payload[Constants.ReservedPayloadUrlField] = url;

        /*
         * PARTITION DETAILS
         */

        record.Vector = partitionEmbedding;
        record.Payload[Constants.ReservedPayloadTextField] = partitionContent;
        record.Payload[Constants.ReservedPayloadVectorProviderField] = embeddingGeneratorProvider;
        record.Payload[Constants.ReservedPayloadVectorGeneratorField] = embeddingGeneratorName;

        // Partition ID. Filtering used for purge.
        record.Tags.Add(Constants.ReservedFilePartitionTag, partitionFileId);

        // Partition number (starting from 0) and Page number (provided by text extractor)
        record.Tags.Add(Constants.ReservedFilePartitionNumberTag, $"{partitionNumber}");
        record.Tags.Add(Constants.ReservedFileSectionNumberTag, $"{sectionNumber}");

        /*
         * TIMESTAMP and USER TAGS
         */

        record.Payload[Constants.ReservedPayloadLastUpdateField] = DateTimeOffset.UtcNow.ToString("s");

        tags.CopyTo(record.Tags);

        return record;
    }
}
