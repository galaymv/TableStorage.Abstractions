﻿using Microsoft.WindowsAzure.Storage.Table;

namespace TableStorage.Abstractions
{
    public class TableStoreFactory : ITableStoreFactory
    {
        public ITableStore<T> CreateTableStore<T>(string tableName, string storageConnectionString) where T : TableEntity, new()
        {
            return new TableStore<T>(tableName, storageConnectionString);
        }

        public ITableStore<T> CreateTableStore<T>(string tableName, string storageConnectionString, int retries, double retryWaitTimeInSeconds) where T : TableEntity, new()
        {
            return new TableStore<T>(tableName, storageConnectionString, retries, retryWaitTimeInSeconds);
        }
    }
}