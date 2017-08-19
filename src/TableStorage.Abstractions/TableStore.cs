﻿using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Useful.Extensions;

namespace TableStorage.Abstractions
{
    /// <summary>
    /// Table store repository
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class TableStore<T> : ITableStore<T> where T : TableEntity, new()
    {
        /// <summary>
        /// The cloud table
        /// </summary>
        private readonly CloudTable _cloudTable;

        /// <summary>
        /// The max size for a single partion to be added to Table Storage
        /// </summary>
        private const int MaxPartitionSize = 100;

        /// <summary>
        /// The default retries
        /// </summary>
        private const int DefaultRetries = 3;

        /// <summary>
        /// The default retry in seconds
        /// </summary>
        private const double DefaultRetryTimeInSeconds = 1;

        #region Construction

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="tableName">The table name</param>
        /// <param name="storageConnectionString">The connection string</param>
        public TableStore(string tableName, string storageConnectionString)
        : this(tableName, storageConnectionString, DefaultRetries, DefaultRetryTimeInSeconds) { }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="tableName">The table name</param>
        /// <param name="storageConnectionString">The connection string</param>
        /// <param name="retries">Number of retries</param>
        /// <param name="retryWaitTimeInSeconds">Wait time between retries in seconds</param>
        public TableStore(string tableName, string storageConnectionString, int retries, double retryWaitTimeInSeconds)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                throw new ArgumentNullException(nameof(tableName));
            }

            if (string.IsNullOrWhiteSpace(storageConnectionString))
            {
                throw new ArgumentNullException(nameof(storageConnectionString));
            }

            OptimisePerformance(storageConnectionString);

            var cloudTableClient = CreateTableClient(storageConnectionString, retries, retryWaitTimeInSeconds);

            _cloudTable = cloudTableClient.GetTableReference(tableName);
            CreateTable();
        }

        /// <summary>
        /// Settings to improve performance
        /// </summary>
        private static void OptimisePerformance(string storageConnectionString)
        {
            var account = CloudStorageAccount.Parse(storageConnectionString);
            var tableServicePoint = ServicePointManager.FindServicePoint(account.TableEndpoint);
            tableServicePoint.UseNagleAlgorithm = false;
            tableServicePoint.Expect100Continue = false;
        }

        /// <summary>
        /// Create the table client
        /// </summary>
        /// <param name="connectionString">The connection string</param>
        /// <param name="retries">Number of retries</param>
        /// <param name="retryWaitTimeInSeconds">Wait time between retries in seconds</param>
        /// <returns>The table client</returns>
        private static CloudTableClient CreateTableClient(string connectionString, int retries, double retryWaitTimeInSeconds)
        {
            var cloudStorageAccount = CloudStorageAccount.Parse(connectionString);

            var requestOptions = new TableRequestOptions
            {
                RetryPolicy = new ExponentialRetry(TimeSpan.FromSeconds(retryWaitTimeInSeconds), retries)
            };

            var cloudTableClient = cloudStorageAccount.CreateCloudTableClient();
            cloudTableClient.DefaultRequestOptions = requestOptions;
            return cloudTableClient;
        }

        #endregion Construction

        #region Synchronous Methods

        /// <summary>
        /// Create the table
        /// </summary>
        public void CreateTable()
        {
            _cloudTable.CreateIfNotExists();
        }

        /// <summary>
        /// Does the table exist
        /// </summary>
        /// <returns></returns>
        public bool TableExists()
        {
            return _cloudTable.Exists();
        }

        /// <summary>
        /// Insert an record
        /// </summary>
        /// <param name="record">The record to insert</param>
        public void Insert(T record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            var operation = TableOperation.Insert(record);
            _cloudTable.Execute(operation);
        }

        /// <summary>
        /// Insert multiple records
        /// </summary>
        /// <param name="records">The records to insert</param>
        public void Insert(IEnumerable<T> records)
        {
            if (records == null)
            {
                throw new ArgumentNullException(nameof(records));
            }
            
            var partitionKeySeparation = records.GroupBy(x => x.PartitionKey)
                .OrderBy(g => g.Key)
                .Select(g => g.AsEnumerable()).SelectMany(entry => entry.Partition(MaxPartitionSize)).ToList();

            foreach (var entry in partitionKeySeparation)
            {
                var operation = new TableBatchOperation();
                entry.ToList().ForEach(operation.Insert);

                if (operation.Any())
                {
                    _cloudTable.ExecuteBatch(operation);
                }
            }
        }

        /// <summary>
        /// Update an record
        /// </summary>
        /// <param name="record">The record to update</param>
        public void Update(T record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            var operation = TableOperation.Merge(record);

            _cloudTable.Execute(operation);
        }

        /// <summary>
        /// Update an record using the wildcard etag
        /// </summary>
        /// <param name="record">The record to update</param>
        public void UpdateUsingWildcardEtag(T record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            record.ETag = "*";
            Update(record);
        }

        /// <summary>
        /// Delete a record
        /// </summary>
        /// <param name="record">The record to delete</param>
        public void Delete(T record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            var operation = TableOperation.Delete(record);
            _cloudTable.Execute(operation);
        }

        /// <summary>
        /// Delete a record using the wildcard etag
        /// </summary>
        /// <param name="record">The record to delete</param>
        public void DeleteUsingWildcardEtag(T record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            record.ETag = "*";
            Delete(record);
        }

        /// <summary>
        /// Delete the table
        /// </summary>
        public void DeleteTable()
        {
            _cloudTable.DeleteIfExists();
        }

        /// <summary>
        /// Get an record by partition and row key
        /// </summary>
        /// <param name="partitionKey"></param>
        /// <param name="rowKey"></param>
        /// <returns>The record found or null if not found</returns>
        public T GetRecord(string partitionKey, string rowKey)
        {
            EnsurePartitionKey(partitionKey);

            EnsureRowKey(rowKey);

            // Create a retrieve operation that takes a customer record.
            var retrieveOperation = TableOperation.Retrieve<T>(partitionKey, rowKey);

            // Execute the operation.
            var retrievedResult = _cloudTable.Execute(retrieveOperation);

            return retrievedResult.Result as T;
        }

        /// <summary>
        /// Get the records by partition key
        /// </summary>
        /// <param name="partitionKey">The partition key</param>
        /// <returns>The records found</returns>
        public IEnumerable<T> GetByPartitionKey(string partitionKey)
        {
            EnsurePartitionKey(partitionKey);

            TableContinuationToken continuationToken = null;

            var query = BuildGetByPartitionQuery(partitionKey);

            var allItems = new List<T>();
            do
            {
                var items = _cloudTable.ExecuteQuerySegmented(query, continuationToken);
                continuationToken = items.ContinuationToken;
                allItems.AddRange(items);
            } while (continuationToken != null);

            return allItems;
        }

        /// <summary>
        /// Get the records by partition key, paged
        /// </summary>
        /// <param name="partitionKey">The partition key.</param>
        /// <param name="pageSize">Size of the page.</param>
        /// <param name="continuationTokenJson">The next page token.</param>
        /// <returns>The Paged Result</returns>
        public PagedResult<T> GetByPartitionKeyPaged(string partitionKey, int pageSize = 100, string continuationTokenJson = null)
        {
            EnsurePartitionKey(partitionKey);

            var continuationToken = DeserializeContinuationToken(continuationTokenJson);

            var query = BuildGetByPartitionQuery(partitionKey);
            query.TakeCount = pageSize;

            var allItems = new List<T>();

            var items = _cloudTable.ExecuteQuerySegmented(query, continuationToken);
            continuationToken = items.ContinuationToken;
            allItems.AddRange(items);

            return CreatePagedResult(continuationToken, allItems);
        }

        /// <summary>
        /// Get the records by row key
        /// </summary>
        /// <param name="rowKey">The row key</param>
        /// <returns>The records found</returns>
        public IEnumerable<T> GetByRowKey(string rowKey)
        {
            EnsureRowKey(rowKey);

            TableContinuationToken continuationToken = null;

            var query = BuildGetByRowKeyQuery(rowKey);

            var allItems = new List<T>();
            do
            {
                var items = _cloudTable.ExecuteQuerySegmented(query, continuationToken);
                continuationToken = items.ContinuationToken;
                allItems.AddRange(items);
            } while (continuationToken != null);

            return allItems;
        }

        /// <summary>
        /// Get the records by row key
        /// </summary>
        /// <param name="rowKey">The row key.</param>
        /// <param name="pageSize">Size of the page.</param>
        /// <param name="continuationTokenJson">The next page token.</param>
        /// <returns>The Paged Result</returns>
        public PagedResult<T> GetByRowKeyPaged(string rowKey, int pageSize = 100, string continuationTokenJson = null)
        {
            EnsureRowKey(rowKey);

            TableContinuationToken continuationToken = DeserializeContinuationToken(continuationTokenJson);

            var query = BuildGetByRowKeyQuery(rowKey);
            query.TakeCount = pageSize;

            var allItems = new List<T>();

            var items = _cloudTable.ExecuteQuerySegmented(query, continuationToken);
            continuationToken = items.ContinuationToken;
            allItems.AddRange(items);

            return CreatePagedResult(continuationToken, allItems);
        }

        /// <summary>
        /// Get all the records in the table
        /// </summary>
        /// <returns>All records</returns>
        public IEnumerable<T> GetAllRecords()
        {
            TableContinuationToken continuationToken = null;

            var query = new TableQuery<T>();

            var allItems = new List<T>();
            do
            {
                var items = _cloudTable.ExecuteQuerySegmented(query, continuationToken);
                continuationToken = items.ContinuationToken;
                allItems.AddRange(items);
            } while (continuationToken != null);

            return allItems;
        }

        public PagedResult<T> GetAllRecordsPaged(int pageSize = 100, string pageToken = null)
        {
            var query = new TableQuery<T> { TakeCount = pageSize };

            var allItems = new List<T>();

            var items = _cloudTable.ExecuteQuerySegmented(query, null);
            var continuationToken = items.ContinuationToken;
            allItems.AddRange(items);
            return CreatePagedResult(continuationToken, allItems);
        }

        /// <summary>
        /// Get the number of the records in the table
        /// </summary>
        /// <returns>The record count</returns>
        public int GetRecordCount()
        {
            TableContinuationToken continuationToken = null;

            var query = new TableQuery<T>().Select(new List<string> { "PartitionKey" });

            var recordCount = 0;
            do
            {
                var items = _cloudTable.ExecuteQuerySegmented(query, continuationToken);
                continuationToken = items.ContinuationToken;

                recordCount += items.Count();
            } while (continuationToken != null);

            return recordCount;
        }

        #endregion Synchronous Methods

        #region Asynchronous Methods

        /// <summary>
        /// Create the table
        /// </summary>
        public async Task CreateTableAsync()
        {
            await _cloudTable.CreateIfNotExistsAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Does the table exist
        /// </summary>
        /// <returns></returns>
        public async Task<bool> TableExistsAsync()
        {
            return await _cloudTable.ExistsAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Insert an record
        /// </summary>
        /// <param name="record">The record to insert</param>
        public async Task InsertAsync(T record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            var operation = TableOperation.Insert(record);

            await _cloudTable.ExecuteAsync(operation).ConfigureAwait(false);
        }

        /// <summary>
        /// Insert multiple records
        /// </summary>
        /// <param name="records">The records to insert</param>
        public async Task InsertAsync(IEnumerable<T> records)
        {
            if (records == null)
            {
                throw new ArgumentNullException(nameof(records));
            }

            var partitionKeySeparation = records.GroupBy(x => x.PartitionKey)
                .OrderBy(g => g.Key)
                .Select(g => g.AsEnumerable()).SelectMany(entry => entry.Partition(MaxPartitionSize)).ToList();

            foreach (var entry in partitionKeySeparation)
            {
                var operation = new TableBatchOperation();
                entry.ToList().ForEach(operation.Insert);

                if (operation.Any())
                {
                    await _cloudTable.ExecuteBatchAsync(operation).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Update an record
        /// </summary>
        /// <param name="record">The record to update</param>
        public async Task UpdateAsync(T record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            var operation = TableOperation.Merge(record);

            await _cloudTable.ExecuteAsync(operation).ConfigureAwait(false);
        }

        /// <summary>
        /// Update an record using the wildcard etag
        /// </summary>
        /// <param name="record">The record to update</param>
        public async Task UpdateUsingWildcardEtagAsync(T record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            record.ETag = "*";
            await UpdateAsync(record).ConfigureAwait(false);
        }

        /// <summary>
        /// Update an entry
        /// </summary>
        /// <param name="record">The record to update</param>
        public async Task DeleteAsync(T record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            var operation = TableOperation.Delete(record);

            await _cloudTable.ExecuteAsync(operation).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete a record using the wildcard etag
        /// </summary>
        /// <param name="record">The record to delete</param>
        public async Task DeleteUsingWildcardEtagAsync(T record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            record.ETag = "*";

            await DeleteAsync(record).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete the table
        /// </summary>
        public async Task DeleteTableAsync()
        {
            await _cloudTable.DeleteIfExistsAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Get an record by partition and row key
        /// </summary>
        /// <param name="partitionKey"></param>
        /// <param name="rowKey"></param>
        /// <returns>The record found or null if not found</returns>
        public async Task<T> GetRecordAsync(string partitionKey, string rowKey)
        {
            EnsurePartitionKey(partitionKey);

            EnsureRowKey(rowKey);

            // Create a retrieve operation that takes a customer record.
            var retrieveOperation = TableOperation.Retrieve<T>(partitionKey, rowKey);

            // Execute the operation.
            var retrievedResult = await _cloudTable.ExecuteAsync(retrieveOperation).ConfigureAwait(false);

            return retrievedResult.Result as T;
        }

        /// <summary>
        /// Get the records by partition key
        /// </summary>
        /// <param name="partitionKey">The partition key</param>
        /// <returns>The records found</returns>
        public async Task<IEnumerable<T>> GetByPartitionKeyAsync(string partitionKey)
        {
            EnsurePartitionKey(partitionKey);

            TableContinuationToken continuationToken = null;

            var query = BuildGetByPartitionQuery(partitionKey);

            var allItems = new List<T>();
            do
            {
                var items = await _cloudTable.ExecuteQuerySegmentedAsync(query, continuationToken).ConfigureAwait(false);
                continuationToken = items.ContinuationToken;
                allItems.AddRange(items);
            } while (continuationToken != null);

            return allItems;
        }

        /// <summary>
        ///  Get the records by partition key, paged
        /// </summary>
        /// <param name="partitionKey">The partition key.</param>
        /// <param name="pageSize">Size of the page.</param>
        /// <param name="continuationTokenJson">The next page token.</param>
        /// <returns>The Paged Result</returns>
        public async Task<PagedResult<T>> GetByPartitionKeyPagedAsync(string partitionKey, int pageSize = 100, string continuationTokenJson = null)
        {
            EnsurePartitionKey(partitionKey);

            var continuationToken = DeserializeContinuationToken(continuationTokenJson);

            var query = BuildGetByPartitionQuery(partitionKey);
            query.TakeCount = pageSize;

            var allItems = new List<T>();

            var items = await _cloudTable.ExecuteQuerySegmentedAsync(query, continuationToken).ConfigureAwait(false);
            continuationToken = items.ContinuationToken;
            allItems.AddRange(items);

            return CreatePagedResult(continuationToken, allItems);
        }

        /// <summary>
        /// Get the records by row key
        /// </summary>
        /// <param name="rowKey">The row key</param>
        /// <returns>The records found</returns>
        public async Task<IEnumerable<T>> GetByRowKeyAsync(string rowKey)
        {
            EnsureRowKey(rowKey);

            TableContinuationToken continuationToken = null;

            var query = BuildGetByRowKeyQuery(rowKey);

            var allItems = new List<T>();
            do
            {
                var items = await _cloudTable.ExecuteQuerySegmentedAsync(query, continuationToken).ConfigureAwait(false);
                continuationToken = items.ContinuationToken;
                allItems.AddRange(items);
            } while (continuationToken != null);

            return allItems;
        }

        /// <summary>
        /// Get the records by row key
        /// </summary>
        /// <param name="rowKey">The row keyint.</param>
        /// <param name="pageSize">Size of the page.</param>
        /// <param name="continuationTokenJson">The next page token.</param>
        public async Task<PagedResult<T>> GetByRowKeyPagedAsync(string rowKey, int pageSize = 100, string continuationTokenJson = null)
        {
            EnsureRowKey(rowKey);

            var continuationToken = DeserializeContinuationToken(continuationTokenJson);

            var query = BuildGetByRowKeyQuery(rowKey);
            query.TakeCount = pageSize;

            var allItems = new List<T>();

            var items = await _cloudTable.ExecuteQuerySegmentedAsync(query, continuationToken).ConfigureAwait(false);
            continuationToken = items.ContinuationToken;
            allItems.AddRange(items);

            return CreatePagedResult(continuationToken, allItems);
        }

        /// <summary>
        /// Get all the records in the table
        /// </summary>
        /// <returns>All records</returns>
        public async Task<IEnumerable<T>> GetAllRecordsAsync()
        {
            TableContinuationToken continuationToken = null;

            var query = new TableQuery<T>();

            var allItems = new List<T>();
            do
            {
                var items = await _cloudTable.ExecuteQuerySegmentedAsync(query, continuationToken).ConfigureAwait(false);
                continuationToken = items.ContinuationToken;
                allItems.AddRange(items);
            } while (continuationToken != null);

            return allItems;
        }

        /// <summary>
        /// Gets all records in the table, paged
        /// </summary>
        /// <returns>The Paged Result</returns>
        public async Task<PagedResult<T>> GetAllRecordsPagedAsync(int pageSize = 100, string pageToken = null)
        {
            var query = new TableQuery<T> { TakeCount = pageSize };

            var allItems = new List<T>();

            var items = await _cloudTable.ExecuteQuerySegmentedAsync(query, null).ConfigureAwait(false);
            var continuationToken = items.ContinuationToken;
            allItems.AddRange(items);
            return CreatePagedResult(continuationToken, allItems);
        }

        /// <summary>
        /// Get the number of the records in the table
        /// </summary>
        /// <returns>The record count</returns>
        public async Task<int> GetRecordCountAsync()
        {
            TableContinuationToken continuationToken = null;

            var query = new TableQuery<T>().Select(new List<string> { "PartitionKey" });

            var recordCount = 0;
            do
            {
                var items = await _cloudTable.ExecuteQuerySegmentedAsync(query, continuationToken).ConfigureAwait(false);
                continuationToken = items.ContinuationToken;

                recordCount += items.Count();
            } while (continuationToken != null);

            return recordCount;
        }

        #endregion Asynchronous Methods

        /// <summary>
        /// Ensures the partition key is not null.
        /// </summary>
        /// <param name="partitionKey">The partition key.</param>
        /// <exception cref="ArgumentNullException">partitionKey</exception>
        private void EnsurePartitionKey(string partitionKey)
        {
            if (string.IsNullOrWhiteSpace(partitionKey))
            {
                throw new ArgumentNullException(nameof(partitionKey));
            }
        }

        /// <summary>
        /// Ensures the row key is not null.
        /// </summary>
        /// <param name="rowKey">The row key.</param>
        /// <exception cref="ArgumentNullException">rowKey</exception>
        private void EnsureRowKey(string rowKey)
        {
            if (string.IsNullOrWhiteSpace(rowKey))
            {
                throw new ArgumentNullException(nameof(rowKey));
            }
        }

        /// <summary>
        /// Builds the get by partition query.
        /// </summary>
        /// <param name="partitionKey">The partition key.</param>
        /// <returns>The table query</returns>
        private static TableQuery<T> BuildGetByPartitionQuery(string partitionKey)
        {
            var query = new TableQuery<T>().Where(
                TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, partitionKey));
            return query;
        }

        /// <summary>
        /// Build the row key table query
        /// </summary>
        /// <param name="rowKey">The row key</param>
        /// <returns>The table query</returns>
        private static TableQuery<T> BuildGetByRowKeyQuery(string rowKey)
        {
            var query = new TableQuery<T>().Where(TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.Equal, rowKey));
            return query;
        }

        /// <summary>
        /// Create a paged result
        /// </summary>
        /// <param name="continuationToken">The continuation token</param>
        /// <param name="items">The items</param>
        /// <returns>The paged result</returns>
        private static PagedResult<T> CreatePagedResult(TableContinuationToken continuationToken, IList<T> items)
        {
            var continuationTokenJson = continuationToken != null ? JsonConvert.SerializeObject(continuationToken) : null;
            return new PagedResult<T>(items, continuationTokenJson, continuationToken == null);
        }

        /// <summary>
        /// Deserialize the continuation token
        /// </summary>
        /// <param name="continuationTokenJson">The json string containing the continuation token</param>
        /// <returns>The continuation token</returns>
        private static TableContinuationToken DeserializeContinuationToken(string continuationTokenJson)
        {
            TableContinuationToken continuationToken = null;
            if (!string.IsNullOrEmpty(continuationTokenJson))
            {
                continuationToken = JsonConvert.DeserializeObject<TableContinuationToken>(continuationTokenJson);
            }
            return continuationToken;
        }
    }
}