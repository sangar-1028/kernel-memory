﻿// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticMemory.Client.Models;

namespace Microsoft.SemanticMemory.Client;

public interface ISemanticMemoryClient
{
    /// <summary>
    /// Import a document into memory. The document can contain one or more files, can have tags and other details.
    /// </summary>
    /// <param name="document">Details of the files to import</param>
    /// <param name="index">Optional index name</param>
    /// <param name="steps">Ingestion pipeline steps, optional override to the system default</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    /// <returns>Document ID</returns>
    public Task<string> ImportDocumentAsync(
        Document document,
        string? index = null,
        IEnumerable<string>? steps = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Import a files from disk into memory, with details such as tags and user ID.
    /// </summary>
    /// <param name="fileName">Path and name of the files to import</param>
    /// <param name="documentId">Document ID</param>
    /// <param name="tags">Optional tags to apply to the memories generated by the document</param>
    /// <param name="index">Optional index name</param>
    /// <param name="steps">Ingestion pipeline steps, optional override to the system default</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    /// <returns>Document ID</returns>
    public Task<string> ImportDocumentAsync(
        string fileName,
        string? documentId = null,
        TagCollection? tags = null,
        string? index = null,
        IEnumerable<string>? steps = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Import a document into memory. The document can contain one or more files, can have tags and other details.
    /// </summary>
    /// <param name="uploadRequest">Upload request containing the document files and details</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    /// <returns>Document ID</returns>
    public Task<string> ImportDocumentAsync(
        DocumentUploadRequest uploadRequest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a document ID exists in a user memory and is ready for usage.
    /// The logic checks if the uploaded document has been fully processed.
    /// When the document exists in storage but is not processed yet, the method returns False.
    /// </summary>
    /// <param name="documentId">Document ID</param>
    /// <param name="index">Optional index name</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    /// <returns>True if the document has been successfully uploaded and imported</returns>
    public Task<bool> IsDocumentReadyAsync(
        string documentId,
        string? index = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get information about an uploaded document
    /// </summary>
    /// <param name="documentId">Document ID (aka pipeline ID)</param>
    /// <param name="index">Optional index name</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    /// <returns>Information about an uploaded document</returns>
    public Task<DataPipelineStatus?> GetDocumentStatusAsync(
        string documentId,
        string? index = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Search the default user memory for a list of relevant documents for the given query.
    /// </summary>
    /// <param name="query">Query to filter memories</param>
    /// <param name="index">Optional index name</param>
    /// <param name="filter">Filter to match</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    /// <returns>Answer to the query, if possible</returns>
    public Task<SearchResult> SearchAsync(
        string query,
        string? index = null,
        MemoryFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Search the default user memory for an answer to the given query.
    /// </summary>
    /// <param name="question">Question to answer</param>
    /// <param name="index">Optional index name</param>
    /// <param name="filter">Filter to match</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    /// <returns>Answer to the query, if possible</returns>
    public Task<MemoryAnswer> AskAsync(
        string question,
        string? index = null,
        MemoryFilter? filter = null,
        CancellationToken cancellationToken = default);
}
