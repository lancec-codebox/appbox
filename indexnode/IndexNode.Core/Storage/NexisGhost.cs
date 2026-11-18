// Ported from Nexis (https://github.com/ljcollins25/codebox), commit 16c933d "Nexis: single-blob hydrate API with tests".
// Source files, in Nexis.Azure.Utilities/Nexis.Azure.Utilities/:
//   Operations/DehydrateFilesOperation.cs  HydrateBlobAsync, HydrateIfGhostAsync, TagNames (class DehydrateOperation)
//   Operations/DriveOperationBase.cs       BaseTags, BlobState, RemoveCustomKeys
//   Helpers.cs                             Strings (the tag names)
//   Timestamp.cs                           Timestamp (the text format of last_access)
// The bodies are copied unchanged; only the surrounding class is cut down to these members.
// The two copies must stay in step: when Nexis changes how a ghost is hydrated or tagged, change this file too.
using System.Collections.Immutable;
using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace Nexis.Azure.Utilities;

// ---- Timestamp.cs (only the members IndexNode needs, text format "o" in UTC) ----
public record struct Timestamp(DateTime Value) : IComparable<Timestamp>
{
    public static readonly Timestamp Zero = new(default);
    public static implicit operator Timestamp(DateTime d) => new(d.ToUniversalTime());
    public static implicit operator Timestamp(DateTimeOffset d) => new(d.UtcDateTime);
    public static implicit operator Timestamp?(string? d) => d == null ? null : new(DateTime.ParseExact(d, "o", null).ToUniversalTime());
    public static implicit operator Timestamp(string d) => new(DateTime.ParseExact(d, "o", null).ToUniversalTime());
    public static implicit operator string(Timestamp d) => d.ToString();

    public static Timestamp Now => DateTime.UtcNow;

    public static Timestamp Parse(string value)
    {
        return value;
    }

    public int CompareTo(Timestamp other)
    {
        return Value.CompareTo(other.Value);
    }

    public override string ToString()
    {
        return Value.ToString("o");
    }
}

// ---- Helpers.cs ----
internal static class Strings
{
    public const string tagPrefix = "ghostd_";
    public const string last_refresh_time = $"{tagPrefix}refresh_time";
    public const string last_access = $"{tagPrefix}last_access";
    public const string snapshot = $"{tagPrefix}snapshot";
    public const string state = $"{tagPrefix}state";
    public const string block_prefix = $"{tagPrefix}block_prefix";
    public const string size = $"{tagPrefix}size";
    public const string dirty_time = $"{tagPrefix}dirty_time";
    public const string dir_metadata = "hdi_isfolder";
    public const string mtime_metadata = "mtime";
}

public static class DehydrateOperation
{
    // ---- DriveOperationBase.cs ----
    private static ImmutableDictionary<string, string> BaseTags = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
        .Add("archive_version", "1")
        ;

    private enum BlobState
    {
        pending,
        ghost,
        transitioning,
        active
    }

    private static ImmutableDictionary<string, string> RemoveCustomKeys(
        IEnumerable<KeyValuePair<string, string>> source,
        BlobState state)
    {
        return BaseTags
            .SetItems(source)
            .RemoveRange(source.Select(s => s.Key).Where(k => k.StartsWith(Strings.tagPrefix)))
            .SetItem(Strings.state, state.ToString());
    }

    // ---- DehydrateFilesOperation.cs ----
    /// Commits the uncommitted blocks of a ghosted blob (state=ghost) back into a committed, active blob.
    /// This is the single-blob hydrate step used by the dehydrate timer and by callers that need one blob.
    /// </summary>
    /// <returns>The ETag of the hydrated blob.</returns>
    public static async Task<ETag?> HydrateBlobAsync(
        BlockBlobClient blobClient,
        IReadOnlyDictionary<string, string> readTags,
        IDictionary<string, string>? metadata,
        BlobRequestConditions? conditions,
        CancellationToken token)
    {
        var blockPrefix = readTags[Strings.block_prefix];
        var currentBlockList = await blobClient.GetBlockListAsync(cancellationToken: token);
        var blocksToCommit = currentBlockList.Value.UncommittedBlocks
            .Where(b => b.Name.StartsWith(blockPrefix))
            .OrderBy(b => b.Name)
            .ToList();

        var totalSize = blocksToCommit.Sum(b => (long?)b.SizeLong) ?? 0;
        var expectedSize = readTags.GetValueOrDefault(Strings.size, "0");
        if (totalSize.ToString() != expectedSize)
        {
            throw new InvalidOperationException($"actual size ({totalSize}) != expected size ({expectedSize})");
        }

        var crsp = await blobClient.CommitBlockListAsync(
            base64BlockIds: blocksToCommit.Select(b => b.Name),
            options: new CommitBlockListOptions()
            {
                Tags = RemoveCustomKeys(readTags, BlobState.active),
                Conditions = conditions,
                Metadata = metadata,
            },
            cancellationToken: token);

        return crsp.Value.ETag;
    }

    /// <summary>
    /// Hydrates one blob if (and only if) it is currently a Nexis ghost. Returns true when it hydrated.
    /// </summary>
    public static async Task<bool> HydrateIfGhostAsync(BlockBlobClient blobClient, CancellationToken token)
    {
        var props = (await blobClient.GetPropertiesAsync(cancellationToken: token)).Value;
        var tags = (await blobClient.GetTagsAsync(cancellationToken: token)).Value.Tags;
        if (props.ContentLength != 0
            || !tags.TryGetValue(Strings.state, out var state)
            || state != nameof(BlobState.ghost))
        {
            return false;
        }

        var conditions = new BlobRequestConditions() { IfMatch = props.ETag };
        await HydrateBlobAsync(blobClient, tags.ToImmutableDictionary(), props.Metadata, conditions, token);
        return true;
    }

    public static class TagNames
    {
        public const string LastAccess = Strings.last_access;
        public const string State = Strings.state;
    }

}
