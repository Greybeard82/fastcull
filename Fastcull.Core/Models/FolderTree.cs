using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Fastcull.Models
{
    /// <summary>
    /// One folder in the scanned tree, with the photo counts underneath it.
    ///
    /// Built from the sequence rather than from the filesystem, deliberately: the tree should
    /// describe what is actually being culled, so a subfolder full of unsupported files does not
    /// appear as an empty branch the user can click into and find nothing.
    /// </summary>
    public sealed class FolderNode
    {
        public FolderNode(
            string name,
            string relativePath,
            int depth,
            int directPhotoCount,
            int totalPhotoCount,
            int firstPhotoIndex,
            IReadOnlyList<FolderNode> children)
        {
            Name = name;
            RelativePath = relativePath;
            Depth = depth;
            DirectPhotoCount = directPhotoCount;
            TotalPhotoCount = totalPhotoCount;
            FirstPhotoIndex = firstPhotoIndex;
            Children = children;
        }

        /// <summary>Folder name alone. The root carries the scanned folder's own name.</summary>
        public string Name { get; }

        /// <summary>Path relative to the scan root; empty for the root itself.</summary>
        public string RelativePath { get; }

        /// <summary>0 for the root, incrementing per level. Drives the display indent.</summary>
        public int Depth { get; }

        /// <summary>Photos sitting directly in this folder, excluding subfolders.</summary>
        public int DirectPhotoCount { get; }

        /// <summary>Photos in this folder and everything beneath it.</summary>
        public int TotalPhotoCount { get; }

        /// <summary>
        /// Position in the sorted sequence of the earliest photo in this subtree, or -1 when the
        /// subtree holds none. This is what makes a folder clickable: selecting it moves the
        /// cursor there rather than filtering, so the cull sequence is never disturbed.
        /// </summary>
        public int FirstPhotoIndex { get; }

        public IReadOnlyList<FolderNode> Children { get; }

        public bool HasChildren => Children.Count > 0;
    }

    /// <summary>One photo's position in the tree: where it sits, and where it sits in the sequence.</summary>
    public readonly record struct FolderTreeEntry(string RelativePath, int SequenceIndex);

    /// <summary>
    /// Builds <see cref="FolderNode"/> trees from a scanned sequence.
    ///
    /// Pure and WinUI-free so the shape of the tree can be tested headlessly - the panel that
    /// renders it lives in the WinUI project, which no test project can reference.
    /// </summary>
    public static class FolderTree
    {
        /// <summary>
        /// Groups the sequence by folder. <paramref name="rootName"/> labels the root node, which
        /// always exists even for an empty sequence so the panel has something to render.
        /// </summary>
        public static FolderNode Build(string rootName, IEnumerable<FolderTreeEntry> entries)
        {
            var root = new Builder(string.IsNullOrWhiteSpace(rootName) ? "(root)" : rootName, string.Empty, 0);

            if (entries is not null)
            {
                foreach (var entry in entries)
                {
                    if (entry.RelativePath is null) continue;

                    // The relative path includes the file name; only its directory part is tree
                    // structure. Both separators are handled because RelativePath is produced by
                    // Path.GetRelativePath on Windows but the tests build paths by hand.
                    var directory = Path.GetDirectoryName(entry.RelativePath) ?? string.Empty;
                    var segments = directory.Split(
                        new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                        StringSplitOptions.RemoveEmptyEntries);

                    var node = root;
                    foreach (var segment in segments)
                        node = node.Child(segment);

                    node.Add(entry.SequenceIndex);
                }
            }

            return root.ToNode();
        }

        /// <summary>
        /// Flattens the tree into display order - a node immediately followed by its subtree -
        /// descending only into folders the caller says are expanded.
        ///
        /// Expansion state is a UI concern and deliberately lives outside the tree itself, so the
        /// same immutable tree can back a panel whose folders open and close.
        /// </summary>
        public static List<FolderNode> Flatten(FolderNode? root, Func<FolderNode, bool> isExpanded)
        {
            var flat = new List<FolderNode>();
            if (root is null) return flat;

            void Walk(FolderNode node)
            {
                flat.Add(node);
                if (!node.HasChildren || !isExpanded(node)) return;

                foreach (var child in node.Children) Walk(child);
            }

            Walk(root);
            return flat;
        }

        private sealed class Builder
        {
            private readonly Dictionary<string, Builder> _children = new(StringComparer.OrdinalIgnoreCase);
            private readonly string _name;
            private readonly string _relativePath;
            private readonly int _depth;

            private int _direct;
            private int _firstIndex = -1;

            public Builder(string name, string relativePath, int depth)
            {
                _name = name;
                _relativePath = relativePath;
                _depth = depth;
            }

            public Builder Child(string segment)
            {
                if (_children.TryGetValue(segment, out var existing)) return existing;

                var path = _relativePath.Length == 0 ? segment : _relativePath + Path.DirectorySeparatorChar + segment;
                var created = new Builder(segment, path, _depth + 1);
                _children[segment] = created;
                return created;
            }

            public void Add(int sequenceIndex)
            {
                _direct++;
                Observe(sequenceIndex);
            }

            private void Observe(int sequenceIndex)
            {
                if (sequenceIndex < 0) return;
                if (_firstIndex < 0 || sequenceIndex < _firstIndex) _firstIndex = sequenceIndex;
            }

            public FolderNode ToNode()
            {
                var children = _children.Values
                    .Select(c => c.ToNode())
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var total = _direct + children.Sum(c => c.TotalPhotoCount);

                // The subtree's earliest photo, which may live in a child rather than here.
                var first = _firstIndex;
                foreach (var child in children)
                {
                    if (child.FirstPhotoIndex < 0) continue;
                    if (first < 0 || child.FirstPhotoIndex < first) first = child.FirstPhotoIndex;
                }

                return new FolderNode(_name, _relativePath, _depth, _direct, total, first, children);
            }
        }
    }
}
