﻿// Copyright (c) Microsoft Corporation. All Rights Reserved.
// Licensed under the MIT License.

using Microsoft.Datasync.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TodoApp.Data.Models;

namespace TodoApp.Data.Services
{
    /// <summary>
    /// An implementation of the <see cref="ITodoService"/> interface that uses
    /// a remote table on a Datasync Service.
    /// </summary>
    public class RemoteTodoService : ITodoService
    {
        /// <summary>
        /// Reference to the client used for datasync operations.
        /// </summary>
        private DatasyncClient _client = null;

        /// <summary>
        /// Reference to the table used for datasync operations.
        /// </summary>
        private IDatasyncTable<TodoItem> _table = null;

        /// <summary>
        /// When set to true, the client and table and both initialized.
        /// </summary>
        private bool _initialized = false;

        /// <summary>
        /// Used for locking the initialization block to ensure only one initialization happens.
        /// </summary>
        private readonly SemaphoreSlim _asyncLock = new(1, 1);

        /// <summary>
        /// An event handler that is triggered when the list of items changes.
        /// </summary>
        public event EventHandler<TodoServiceEventArgs> TodoItemsUpdated;

        /// <summary>
        /// Initialize the connection to the remote table.
        /// </summary>
        /// <returns></returns>
        private async Task InitializeAsync()
        {
            // Short circuit, in case we are already initialized.
            if (_initialized)
            {
                return;
            }

            try
            {
                // Wait to get the async initialization lock
                await _asyncLock.WaitAsync().ConfigureAwait(false);
                if (_initialized)
                {
                    // This will also execute the async lock.
                    return;
                }

                // Initialize the client.
                _client = new DatasyncClient(Constants.ServiceUri);
                _table = _client.GetTable<TodoItem>();

                // Set _initialied to true to prevent duplication of locking.
                _initialized = true;
            }
            catch (Exception)
            {
                // Re-throw the exception.
                throw;
            }
            finally
            {
                _asyncLock.Release();
            }
        }

        /// <summary>
        /// Get all the items in the list.
        /// </summary>
        /// <returns>The list of items (asynchronously)</returns>
        public async Task<IEnumerable<TodoItem>> GetItemsAsync()
        {
            await InitializeAsync().ConfigureAwait(false);

            var enumerable = _table.GetAsyncItems<TodoItem>();
            // The ToListAsync() method is a part of System.Linq.Async
            return await enumerable.ToListAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Refreshes the TodoItems list manually.
        /// </summary>
        /// <returns>A task that completes when the refresh is done.</returns>
        public async Task RefreshItemsAsync()
        {
            await InitializeAsync().ConfigureAwait(false);

            // Remote table doesn't need to refresh the local data.
            return;
        }

        /// <summary>
        /// Removes an item in the list, if it exists.
        /// </summary>
        /// <param name="item">The item to be removed.</param>
        /// <returns>A task that completes when the item is removed.</returns>
        public async Task RemoveItemAsync(TodoItem item)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (item.Id == null)
            {
                // Short circuit for when the item has not been saved yet.
                return;
            }
            await InitializeAsync().ConfigureAwait(false);

            var response = await _table.DeleteItemAsync(item.Id, IfMatch.Version(item.Version)).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApplicationException($"Item cannot be deleted: {response.ReasonPhrase}");
            }
            TodoItemsUpdated?.Invoke(this, new TodoServiceEventArgs(TodoServiceEventArgs.ListAction.Delete, item));
        }

        /// <summary>
        /// Saves an item to the list.  If the item does not have an Id, then the item
        /// is considered new and will be added to the end of the list.  Otherwise, the
        /// item is considered existing and is replaced.
        /// </summary>
        /// <param name="item">The new item</param>
        /// <returns>A task that completes when the item is saved.</returns>
        public async Task SaveItemAsync(TodoItem item)
        {
            if (item == null)
            {
                throw new ArgumentException(nameof(item));
            }

            await InitializeAsync().ConfigureAwait(false);

            TodoServiceEventArgs.ListAction action = (item.Id == null) ? TodoServiceEventArgs.ListAction.Add : TodoServiceEventArgs.ListAction.Update;
            var response = (item.Id == null)
                ? await _table.CreateItemAsync(item).ConfigureAwait(false)
                : await _table.ReplaceItemAsync(item, IfMatch.Version(item.Version)).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApplicationException($"Item cannot be saved: {response.ReasonPhrase}");
            }
            TodoItemsUpdated?.Invoke(this, new TodoServiceEventArgs(action, response.Value));
        }
    }
}
