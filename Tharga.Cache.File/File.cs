using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Tharga.Cache.Core;

namespace Tharga.Cache.File;

internal class File : IFile
{
    private readonly IFileService _fileService;
    private readonly IFileFormatService _fileFormatService;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly FileCacheOptions _options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _entryLocks = new(StringComparer.OrdinalIgnoreCase);

    public File(IManagedCacheMonitor cacheMonitor, IFileService fileService, IFileFormatService fileFormatService, IHostEnvironment hostEnvironment, IOptions<FileCacheOptions> options)
    {
        _fileService = fileService;
        _fileFormatService = fileFormatService;
        _hostEnvironment = hostEnvironment;
        _options = options.Value;

        cacheMonitor.RequestEvictEvent += async (_, e) =>
        {
            var dropAsyncMethod = typeof(File)
                .GetMethod("DropAsync")!
                .MakeGenericMethod(e.Type);

            var task = (Task)dropAsyncMethod.Invoke(this, [e.Key])!;
            await task;

            cacheMonitor.Drop(e.Type, e.Key);
        };
    }

    public async Task<CacheItem<T>> GetAsync<T>(Key key)
    {
        var fileExtension = _fileFormatService.GetFileExtensions(_options.Format);
        var buildFilename = BuildFilename(key, typeof(T), fileExtension);
        var dataAsync = await _fileService.GetDataAsync(buildFilename);
        var data = _fileFormatService.Unpack<T>(dataAsync);
        return data;
    }

    public async IAsyncEnumerable<(Key Key, CacheItem<T> CacheItem)> FindAsync<T>(Key key)
    {
        //var datas = _datas
        //    .Where(x => x.Value.GetType() == typeof(CacheItem<T>))
        //    .Where(x =>
        //        key.KeyParts
        //            .All(kvp =>
        //                x.Value.KeyParts.TryGetValue(kvp.Key, out var value) &&
        //                value == kvp.Value
        //            )
        //    );

        //foreach (var data in datas)
        //{
        //    yield return (data.Key, (CacheItem<T>)data.Value);
        //}
        Debugger.Break();
        throw new NotImplementedException($"{nameof(FindAsync)} has not yet been impleented for {nameof(IFile)}.");
        yield break;
    }

    public async Task SetAsync<T>(Key key, CacheItem<T> item, bool staleWhileRevalidate)
    {
        await InEntryLockAsync<T, bool>(key, async () =>
        {
            var response = _fileFormatService.Pack(item);
            await _fileService.SetDataAsync(BuildFilename(key, typeof(T), response.FileExtension), response.Data);
            return true;
        });
    }

    public Task<bool> BuyMoreTime<T>(Key key)
    {
        return InEntryLockAsync<T, bool>(key, () => SetUpdateTimeAsync<T>(key, DateTime.UtcNow));
    }

    /// <summary>
    /// A local read is cheap, so the file is read again, and only touched if it is still the entry the caller read.
    /// The read and the write happen under the entry's lock, so within this process a concurrent replace or
    /// invalidate is left alone. Processes that share the cache folder do not share the lock.
    /// </summary>
    public Task<bool> BuyMoreTime<T>(Key key, CacheItem<T> cacheItem, bool staleWhileRevalidate)
    {
        return InEntryLockAsync<T, bool>(key, async () =>
        {
            var current = await GetAsync<T>(key);
            if (!current.IsSameEntryAs(cacheItem)) return false;

            await WriteUpdateTimeAsync(key, current, DateTime.UtcNow);
            return true;
        });
    }

    public Task<bool> Invalidate<T>(Key key)
    {
        return InEntryLockAsync<T, bool>(key, () => SetUpdateTimeAsync<T>(key, DateTime.MinValue));
    }

    public Task<bool> DropAsync<T>(Key key)
    {
        return InEntryLockAsync<T, bool>(key, () =>
        {
            var fileExtension = _fileFormatService.GetFileExtensions(_options.Format);
            return _fileService.DeleteDataAsync(BuildFilename(key, typeof(T), fileExtension));
        });
    }

    public Task<(bool Success, string Message)> CanConnectAsync()
    {
        return _fileService.CanConnectAsync(BuildFilename(Guid.NewGuid().ToString(), null, "txt"));
    }

    private async Task<bool> SetUpdateTimeAsync<T>(Key key, DateTime updateTime)
    {
        var item = await GetAsync<T>(key);
        if (item != null)
        {
            await WriteUpdateTimeAsync(key, item, updateTime);
            return true;
        }

        return false;
    }

    private async Task WriteUpdateTimeAsync<T>(Key key, CacheItem<T> item, DateTime updateTime)
    {
        var updatedItem = item with { UpdateTime = updateTime };
        var response = _fileFormatService.Pack(updatedItem);
        await _fileService.SetDataAsync(BuildFilename(key, typeof(T), response.FileExtension), response.Data);
    }

    /// <summary>
    /// Serializes the writes of one entry within this process, so a read-then-write cannot interleave with another
    /// write of the same entry.
    /// </summary>
    private async Task<TResult> InEntryLockAsync<T, TResult>(Key key, Func<Task<TResult>> action)
    {
        var entryLock = _entryLocks.GetOrAdd($"{typeof(T).FullName}|{key.Value}", _ => new SemaphoreSlim(1, 1));
        await entryLock.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            entryLock.Release();
        }
    }

    private string BuildFilename(Key key, Type type, string fileExtension)
    {
        string[] names;
        if (!string.IsNullOrEmpty(_options.CompanyName) || !string.IsNullOrEmpty(_options.AppName))
        {
            names = [_options.CompanyName, _options.AppName];
        }
        else
        {
            var appName = _options.AppName ?? Assembly.GetEntryAssembly()?.GetName().Name ?? "Unknown";
            names = appName.Split('.');
        }

        var parts = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        var combined = parts
            .Concat(names)
            .Concat([_hostEnvironment.IsProduction() ? null : _hostEnvironment.EnvironmentName])
            .Concat(["Cache", type?.Name, $"{key.Value}.{fileExtension}"])
            .Where(x => !string.IsNullOrEmpty(x))
            .ToArray();

        var cachePath = Path.Combine(combined)
            .Replace("`1", "s");

        if (!_fileFormatService.IsPathCharactersValid(cachePath))
        {
            Debugger.Break();
            throw new InvalidOperationException($"Invalid filepath '{cachePath}'.");
        }

        return cachePath;
    }
}