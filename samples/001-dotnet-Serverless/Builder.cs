﻿// Copyright (c) Microsoft. All rights reserved.

using Microsoft.SemanticKernel.AI.Embeddings;
using Microsoft.SemanticMemory.Core.AI.AzureOpenAI;
using Microsoft.SemanticMemory.Core.AI.OpenAI;
using Microsoft.SemanticMemory.Core.AppBuilders;
using Microsoft.SemanticMemory.Core.Configuration;
using Microsoft.SemanticMemory.Core.ContentStorage.AzureBlobs;
using Microsoft.SemanticMemory.Core.ContentStorage.FileSystemStorage;
using Microsoft.SemanticMemory.Core.MemoryStorage;
using Microsoft.SemanticMemory.Core.MemoryStorage.AzureCognitiveSearch;
using Microsoft.SemanticMemory.Core.Pipeline;
using Microsoft.SemanticMemory.Core.Search;

/// <summary>
/// Flexible dependency injection using dependencies defined in appsettings.json
/// </summary>
public static class Builder
{
    private const string ConfigRoot = "SemanticMemory";

    public static IServiceProvider GetServiceProvider()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        SemanticMemoryConfig config = builder.Configuration.GetSection(ConfigRoot).Get<SemanticMemoryConfig>()
                                      ?? throw new ConfigurationException("Configuration is null");

        builder.Services.AddSingleton<SemanticMemoryConfig>(config);

        ConfigureStorage(builder, config);
        ConfigureIngestion(builder, config);
        ConfigureRetrieval(builder, config);

        return builder.Build().Services;
    }

    /// <summary>
    /// Example of a simple setup
    /// </summary>
    public static IServiceProvider GetServiceProviderSimpleSetupExample()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        SemanticMemoryConfig config = builder.Configuration.GetSection(ConfigRoot).Get<SemanticMemoryConfig>()
                                      ?? throw new ConfigurationException("Configuration is null");

        builder.Services.AddSingleton<SemanticMemoryConfig>(config);

        builder.Services.AddSingleton<InProcessPipelineOrchestrator, InProcessPipelineOrchestrator>();

        // Storage
        builder.Services.AddAzureBlobAsContentStorage(new AzureBlobConfig());

        // Vector DB
        var vectorDbServices = new ConfiguredServices<ISemanticMemoryVectorDb>();
        builder.Services.AddSingleton(vectorDbServices);

        vectorDbServices.AddAzureCognitiveSearchAsVectorDbToList(new AzureCognitiveSearchConfig( /*...*/));
        builder.Services.AddAzureCognitiveSearchAsVectorDb(new AzureCognitiveSearchConfig( /*...*/));

        // Embedding generation
        var embeddingGenerationServices = new ConfiguredServices<ITextEmbeddingGeneration>();
        builder.Services.AddSingleton(embeddingGenerationServices);

        embeddingGenerationServices.AddAzureOpenAIEmbeddingGenerationToList(new AzureOpenAIConfig( /*...*/));
        builder.Services.AddAzureOpenAIEmbeddingGeneration(new AzureOpenAIConfig( /*...*/));

        // Text generation
        builder.Services.AddSemanticKernelWithAzureOpenAI(new AzureOpenAIConfig( /*...*/));

        return builder.Build().Services;
    }

    private static void ConfigureStorage(WebApplicationBuilder builder, SemanticMemoryConfig config)
    {
        // Service where documents and temporary files are stored
        switch (config.ContentStorageType)
        {
            case string x when x.Equals("AzureBlobs", StringComparison.OrdinalIgnoreCase):
                builder.Services.AddAzureBlobAsContentStorage(builder.Configuration
                    .GetSection(ConfigRoot).GetSection("Services").GetSection("AzureBlobs")
                    .Get<AzureBlobConfig>()!);
                break;

            case string x when x.Equals("FileSystemContentStorage", StringComparison.OrdinalIgnoreCase):
                builder.Services.AddFileSystemAsContentStorage(builder.Configuration
                    .GetSection(ConfigRoot).GetSection("Services").GetSection("FileSystemContentStorage")
                    .Get<FileSystemConfig>()!);
                break;

            default:
                throw new NotSupportedException($"Unknown/unsupported {config.ContentStorageType} content storage");
        }
    }

    private static void ConfigureIngestion(WebApplicationBuilder builder, SemanticMemoryConfig config)
    {
        // Hardcoded type, not configurable
        builder.Services.AddSingleton<InProcessPipelineOrchestrator, InProcessPipelineOrchestrator>();

        // List of embedding generators to use (multiple generators allowed during ingestion)
        var embeddingGenerationServices = new ConfiguredServices<ITextEmbeddingGeneration>();
        builder.Services.AddSingleton(embeddingGenerationServices);
        foreach (var type in config.DataIngestion.EmbeddingGeneratorTypes)
        {
            switch (type)
            {
                case string x when x.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase):
                case string y when y.Equals("AzureOpenAIEmbedding", StringComparison.OrdinalIgnoreCase):
                    embeddingGenerationServices.AddAzureOpenAIEmbeddingGenerationToList(builder.Configuration
                        .GetSection(ConfigRoot).GetSection("Services").GetSection("AzureOpenAIEmbedding")
                        .Get<AzureOpenAIConfig>()!);
                    break;

                case string x when x.Equals("OpenAI", StringComparison.OrdinalIgnoreCase):
                    embeddingGenerationServices.AddOpenAITextEmbeddingGenerationToList(builder.Configuration
                        .GetSection(ConfigRoot).GetSection("Services").GetSection("OpenAI")
                        .Get<OpenAIConfig>()!);
                    break;

                default:
                    throw new NotSupportedException($"Unknown/unsupported {type} embedding generator");
            }
        }

        // List of Vector DB list where to store embeddings (multiple DBs allowed during ingestion)
        var vectorDbServices = new ConfiguredServices<ISemanticMemoryVectorDb>();
        builder.Services.AddSingleton(vectorDbServices);
        foreach (var type in config.DataIngestion.VectorDbTypes)
        {
            switch (type)
            {
                case string x when x.Equals("AzureCognitiveSearch", StringComparison.OrdinalIgnoreCase):
                    vectorDbServices.AddAzureCognitiveSearchAsVectorDbToList(builder.Configuration
                        .GetSection(ConfigRoot).GetSection("Services").GetSection("AzureCognitiveSearch")
                        .Get<AzureCognitiveSearchConfig>()!);
                    break;

                default:
                    throw new NotSupportedException($"Unknown/unsupported {type} vector DB");
            }
        }
    }

    private static void ConfigureRetrieval(WebApplicationBuilder builder, SemanticMemoryConfig config)
    {
        builder.Services.AddSearchClient();

        // How to generate embeddings when searching for an answer
        switch (config.Retrieval.EmbeddingGeneratorType)
        {
            case string x when x.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase):
            case string y when y.Equals("AzureOpenAIEmbedding", StringComparison.OrdinalIgnoreCase):
                builder.Services.AddAzureOpenAIEmbeddingGeneration(builder.Configuration
                    .GetSection(ConfigRoot).GetSection("Services").GetSection("AzureOpenAIEmbedding")
                    .Get<AzureOpenAIConfig>()!);
                break;

            case string x when x.Equals("OpenAI", StringComparison.OrdinalIgnoreCase):
                builder.Services.AddOpenAITextEmbeddingGeneration(builder.Configuration
                    .GetSection(ConfigRoot).GetSection("Services").GetSection("OpenAI")
                    .Get<OpenAIConfig>()!);
                break;

            default:
                throw new NotSupportedException($"Unknown/unsupported {config.Retrieval.EmbeddingGeneratorType} text generator");
        }

        // Where to search embeddings when searching for an answer
        switch (config.Retrieval.VectorDbType)
        {
            case string x when x.Equals("AzureCognitiveSearch", StringComparison.OrdinalIgnoreCase):
                builder.Services.AddAzureCognitiveSearchAsVectorDb(builder.Configuration
                    .GetSection(ConfigRoot).GetSection("Services").GetSection("AzureCognitiveSearch")
                    .Get<AzureCognitiveSearchConfig>()!);
                break;

            default:
                throw new NotSupportedException($"Unknown/unsupported {config.Retrieval.VectorDbType} vector DB");
        }

        // How to generate an answer
        switch (config.Retrieval.TextGeneratorType)
        {
            case string x when x.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase):
            case string y when y.Equals("AzureOpenAIText", StringComparison.OrdinalIgnoreCase):
                builder.Services.AddSemanticKernelWithAzureOpenAI(builder.Configuration
                    .GetSection(ConfigRoot).GetSection("Services").GetSection("AzureOpenAIText")
                    .Get<AzureOpenAIConfig>()!);
                break;

            case string x when x.Equals("OpenAI", StringComparison.OrdinalIgnoreCase):
                builder.Services.AddSemanticKernelWithOpenAI(builder.Configuration
                    .GetSection(ConfigRoot).GetSection("Services").GetSection("OpenAI")
                    .Get<OpenAIConfig>()!);
                break;

            default:
                throw new NotSupportedException($"Unknown/unsupported {config.Retrieval.TextGeneratorType} text generator");
        }
    }
}
