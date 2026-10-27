namespace Tharga.Cache.Tests.Helper;

internal record RecordingCache(ITimeCache Cache, RecordingPersist Persist, ICacheMonitor Monitor);
