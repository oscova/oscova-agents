using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using CommunityToolkit.VectorData.SqliteVec;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Vector
{
    /// <summary>
    /// Wraps the SQLite vector store used by framework memory and retrieval features.
    /// </summary>
    public sealed class VectorStoreService : IAsyncDisposable
    {
        private readonly SqliteVectorStore _vectorStore;

        /// <summary>Creates a vector store using the connection policy supplied by its owner.</summary>
        /// <param name="vectorDbPath">The database path whose parent directory must exist.</param>
        /// <param name="connectionString">The complete unpooled connection string, including any engine key.</param>
        public VectorStoreService(string vectorDbPath, string connectionString)
        {
            _vectorStore = new SqliteVectorStore(connectionString);
            var directoryPath = Path.GetDirectoryName(vectorDbPath);
            if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        /// <summary>Checks for a backing collection without creating it.</summary>
        public Task<bool> CollectionExistsAsync(string collectionName, CancellationToken cancellationToken = default) =>
            _vectorStore.CollectionExistsAsync(collectionName, cancellationToken);

        /// <summary>
        /// Gets or creates a typed vector collection.
        /// </summary>
        /// <typeparam name="TKey">The record key type.</typeparam>
        /// <typeparam name="TRecord">The vector record type.</typeparam>
        /// <param name="collectionName">The collection name in the vector database.</param>
        /// <param name="definition">An optional collection definition used when opening the collection.</param>
        /// <returns>The ensured vector collection.</returns>
        public async Task<VectorStoreCollection<TKey, TRecord>> GetCollectionAsync<TKey, TRecord>(string collectionName, VectorStoreCollectionDefinition? definition = null)
            where TKey : notnull
            where TRecord : class
        {
            var collection = _vectorStore.GetCollection<TKey, TRecord>(collectionName, definition);
            await collection.EnsureCollectionExistsAsync().ConfigureAwait(false);
            return collection;
        }

        /// <summary>
        /// Deletes a vector collection if it exists.
        /// </summary>
        /// <typeparam name="TKey">The record key type.</typeparam>
        /// <typeparam name="TRecord">The vector record type.</typeparam>
        /// <param name="collectionName">The collection name to delete.</param>
        /// <param name="definition">The collection definition used to identify vector shadow tables.</param>
        /// <returns>A task that completes when deletion has been requested.</returns>
        public async Task DeleteCollectionAsync<TKey, TRecord>(
            string collectionName,
            VectorStoreCollectionDefinition definition)
            where TKey : notnull
            where TRecord : class
        {
            var collection = _vectorStore.GetCollection<TKey, TRecord>(collectionName, definition);
            await collection.EnsureCollectionDeletedAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Deletes one record from a vector collection.
        /// </summary>
        /// <typeparam name="TKey">The record key type.</typeparam>
        /// <typeparam name="TRecord">The vector record type.</typeparam>
        /// <param name="collectionName">The collection name containing the record.</param>
        /// <param name="key">The key of the record to delete.</param>
        /// <param name="definition">An optional collection definition used when opening the collection.</param>
        /// <returns>A task that completes when the delete operation finishes.</returns>
        public async Task DeleteAsync<TKey, TRecord>(string collectionName, TKey key, VectorStoreCollectionDefinition? definition = null)
            where TKey : notnull
            where TRecord : class
        {
            var collection = _vectorStore.GetCollection<TKey, TRecord>(collectionName, definition);
            await collection.DeleteAsync(key).ConfigureAwait(false);
        }

        /// <summary>Deletes every record matching a filter in bounded batches.</summary>
        /// <typeparam name="TKey">The record key type.</typeparam>
        /// <typeparam name="TRecord">The vector record type.</typeparam>
        /// <param name="collectionName">The collection to update.</param>
        /// <param name="filter">The record filter.</param>
        /// <param name="keySelector">Selects the key used for deletion.</param>
        /// <param name="definition">An optional collection definition used when opening the collection.</param>
        /// <param name="cancellationToken">Cancels enumeration and deletion.</param>
        /// <returns>The number of deleted records.</returns>
        public async Task<int> DeleteWhereAsync<TKey, TRecord>(
            string collectionName,
            Expression<Func<TRecord, bool>> filter,
            Func<TRecord, TKey> keySelector,
            VectorStoreCollectionDefinition? definition = null,
            CancellationToken cancellationToken = default)
            where TKey : notnull
            where TRecord : class
        {
            const int batchSize = 200;
            var deletedCount = 0;

            var collection = await GetCollectionAsync<TKey, TRecord>(
                collectionName,
                definition).ConfigureAwait(false);

            while (true)
            {
                var records = await collection
                    .GetAsync(
                        filter,
                        batchSize,
                        options: null,
                        cancellationToken)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (records.Count == 0)
                {
                    return deletedCount;
                }

                var keys = records.ConvertAll(record => keySelector(record));
                await collection
                    .DeleteAsync(keys, cancellationToken)
                    .ConfigureAwait(false);
                deletedCount += keys.Count;
            }
        }

        /// <summary>
        /// Upserts a batch of records into a vector collection.
        /// </summary>
        /// <typeparam name="TKey">The record key type.</typeparam>
        /// <typeparam name="TRecord">The vector record type.</typeparam>
        /// <param name="collectionName">The collection name to update.</param>
        /// <param name="records">The records to upsert.</param>
        /// <param name="definition">An optional collection definition used when opening the collection.</param>
        /// <returns>A task that completes when the batch has been upserted.</returns>
        public async Task UpdateBatchAsync<TKey, TRecord>(string collectionName, IEnumerable<TRecord> records, VectorStoreCollectionDefinition? definition = null)
            where TKey : notnull
            where TRecord : class
        {
            var collection = await GetCollectionAsync<TKey, TRecord>(collectionName, definition).ConfigureAwait(false);
            await collection.UpsertAsync(records).ConfigureAwait(false);
        }

        /// <summary>
        /// Searches a vector collection for records nearest to an embedding.
        /// </summary>
        /// <typeparam name="TKey">The record key type.</typeparam>
        /// <typeparam name="TRecord">The vector record type.</typeparam>
        /// <param name="collectionName">The collection name to search.</param>
        /// <param name="embedding">The query embedding.</param>
        /// <param name="topK">The maximum number of nearest results to return.</param>
        /// <param name="options">Optional vector search options.</param>
        /// <param name="definition">An optional collection definition used when opening the collection.</param>
        /// <returns>The nearest vector search results as a materialized list.</returns>
        public async Task<IReadOnlyList<VectorSearchResult<TRecord>>> SearchAsync<TKey, TRecord>(string collectionName, Embedding<float> embedding, int topK, VectorSearchOptions<TRecord>? options = null, VectorStoreCollectionDefinition? definition = null)
            where TKey : notnull
            where TRecord : class
        {
            var resultList = new List<VectorSearchResult<TRecord>>();
            var collection = await GetCollectionAsync<TKey, TRecord>(collectionName, definition).ConfigureAwait(false);
            var searchResult = collection.SearchAsync(embedding, topK, options);

            await foreach (var result in searchResult.ConfigureAwait(false))
            {
                resultList.Add(result);
            }

            return resultList;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            _vectorStore.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
