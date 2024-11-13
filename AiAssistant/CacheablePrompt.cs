namespace AiAssistant;

using System;
using System.Collections.Generic;

using Microsoft.Extensions.VectorData;

sealed record CacheablePrompt
{
    [VectorStoreRecordKey]
    public String Id { get; set; } = Guid.NewGuid().ToString();
    [VectorStoreRecordData(IsFullTextSearchable = true)]
    public String Prompt { get; set; } = String.Empty;
    [VectorStoreRecordVector(Dimensions: 1024, DistanceFunction.CosineDistance, IndexKind.Hnsw)]
    public ReadOnlyMemory<Single> PromptEmbedding { get; set; }
    [VectorStoreRecordData(IsFilterable = true)]
    public String Response { get; set; } = String.Empty;
    //[VectorStoreRecordData(IsFilterable = true)]
    //public required String[] Tags { get; set; }
}
