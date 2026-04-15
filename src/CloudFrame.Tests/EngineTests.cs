using System.Collections.Generic;
using CloudFrame.Core.Cloud;
using CloudFrame.Core.Config;
using CloudFrame.Core.Filtering;
using CloudFrame.Core.Index;
using Xunit;

namespace CloudFrame.Tests
{
    public class FilterEngineTests
    {
        // ── Glob patterns ─────────────────────────────────────────────────────

        [Theory]
        [InlineData("*.arw",           "IMG_001.arw",           true)]
        [InlineData("*.arw",           "IMG_001.jpg",           false)]
        [InlineData("*thumbnail*",     "IMG_001_thumbnail.jpg", true)]
        [InlineData("*thumbnail*",     "IMG_001.jpg",           false)]
        [InlineData("*/private/*",     "Holidays/private/IMG.jpg", true)]
        [InlineData("*/private/*",     "Holidays/public/IMG.jpg",  false)]
        [InlineData("**/*.arw",        "Holidays/2023/RAW/IMG.arw", true)]
        [InlineData("**/*.arw",        "Holidays/2023/RAW/IMG.jpg", false)]
        public void GlobExcludeRule_MatchesExpected(
            string pattern, string path, bool shouldDeny)
        {
            var engine = new FilterEngine(
            [
                new FilterRule
                {
                    Name = "test",
                    Action = FilterAction.Exclude,
                    PatternType = FilterPatternType.Glob,
                    Pattern = pattern,
                    IsEnabled = true
                }
            ]);

            // If shouldDeny, IsAllowed must return false.
            Assert.Equal(!shouldDeny, engine.IsAllowed(path));
        }

        [Fact]
        public void NoRules_AllowsEverything()
        {
            var engine = new FilterEngine([]);
            Assert.True(engine.IsAllowed("Holidays/2023/IMG_001.jpg"));
        }

        [Fact]
        public void OnlyExcludeRules_AllowsNonMatchingImages()
        {
            var engine = new FilterEngine(
            [
                new FilterRule
                {
                    Name = "no raw",
                    Action = FilterAction.Exclude,
                    PatternType = FilterPatternType.Glob,
                    Pattern = "*.arw",
                    IsEnabled = true
                }
            ]);

            Assert.True(engine.IsAllowed("Holidays/IMG.jpg"));
            Assert.False(engine.IsAllowed("Holidays/IMG.arw"));
        }

        [Fact]
        public void OnlyIncludeRules_DeniesNonMatchingImages()
        {
            var engine = new FilterEngine(
            [
                new FilterRule
                {
                    Name = "only jpg",
                    Action = FilterAction.Include,
                    PatternType = FilterPatternType.Glob,
                    Pattern = "*.jpg",
                    IsEnabled = true
                }
            ]);

            Assert.True(engine.IsAllowed("IMG.jpg"));
            Assert.False(engine.IsAllowed("IMG.png"));
            Assert.False(engine.IsAllowed("IMG.arw"));
        }

        [Fact]
        public void FirstMatchWins_IncludeBeforeExclude()
        {
            // Include rule for "favourites" folder, then exclude *.arw globally.
            // A .arw file in favourites should be INCLUDED (first rule wins).
            var engine = new FilterEngine(
            [
                new FilterRule
                {
                    Name = "always include favourites",
                    Action = FilterAction.Include,
                    PatternType = FilterPatternType.Glob,
                    Pattern = "*/favourites/*",
                    IsEnabled = true
                },
                new FilterRule
                {
                    Name = "no raw anywhere else",
                    Action = FilterAction.Exclude,
                    PatternType = FilterPatternType.Glob,
                    Pattern = "*.arw",
                    IsEnabled = true
                }
            ]);

            Assert.True(engine.IsAllowed("Holidays/favourites/IMG.arw"));
            Assert.False(engine.IsAllowed("Holidays/other/IMG.arw"));
        }

        [Fact]
        public void DisabledRule_IsSkipped()
        {
            var engine = new FilterEngine(
            [
                new FilterRule
                {
                    Name = "disabled exclude",
                    Action = FilterAction.Exclude,
                    PatternType = FilterPatternType.Glob,
                    Pattern = "*.jpg",
                    IsEnabled = false   // disabled — should not apply
                }
            ]);

            Assert.True(engine.IsAllowed("IMG.jpg"));
        }

        [Fact]
        public void RegexRule_MatchesExpected()
        {
            var engine = new FilterEngine(
            [
                new FilterRule
                {
                    Name = "exclude 2020",
                    Action = FilterAction.Exclude,
                    PatternType = FilterPatternType.Regex,
                    Pattern = @"/2020/",
                    IsEnabled = true
                }
            ]);

            Assert.False(engine.IsAllowed("Holidays/2020/IMG.jpg"));
            Assert.True(engine.IsAllowed("Holidays/2023/IMG.jpg"));
        }
    }

    // ── ImageIndex tests ──────────────────────────────────────────────────────

    public class ImageIndexTests
    {
        private static CloudImageEntry MakeEntry(string accountId, string name)
            => new()
            {
                Id = name,
                Name = name,
                RelativePath = name,
                AccountId = accountId
            };

        [Fact]
        public void Empty_ReturnsNull()
        {
            Assert.Null(ImageIndex.Empty.Pick());
        }

        [Fact]
        public void Build_FiltersOutDisabledAccounts()
        {
            var config = new AccountConfig
            {
                AccountId = "acc1",
                IsEnabled = false
            };

            var index = ImageIndex.Build(
            [
                (config, new List<CloudImageEntry> { MakeEntry("acc1", "IMG.jpg") })
            ]);

            Assert.Equal(0, index.TotalCount);
        }

        [Fact]
        public void Build_AppliesFilterRules()
        {
            var config = new AccountConfig
            {
                AccountId = "acc1",
                IsEnabled = true,
                FilterRules =
                [
                    new FilterRule
                    {
                        Name = "no arw",
                        Action = FilterAction.Exclude,
                        PatternType = FilterPatternType.Glob,
                        Pattern = "*.arw",
                        IsEnabled = true
                    }
                ]
            };

            var entries = new List<CloudImageEntry>
            {
                MakeEntry("acc1", "IMG001.jpg"),
                MakeEntry("acc1", "IMG001.arw")
            };

            var index = ImageIndex.Build([(config, entries)]);

            // Only the .jpg should remain.
            Assert.Equal(1, index.TotalCount);
        }

        [Fact]
        public void Pick_ReturnsEntryFromNonEmptyIndex()
        {
            var config = new AccountConfig
            {
                AccountId = "acc1",
                IsEnabled = true
            };

            var index = ImageIndex.Build(
            [
                (config, new List<CloudImageEntry> { MakeEntry("acc1", "IMG.jpg") })
            ]);

            Assert.NotNull(index.Pick());
        }

        [Fact]
        public void PickMany_ReturnsAtMostTotalCount()
        {
            var config = new AccountConfig { AccountId = "acc1", IsEnabled = true };
            var entries = new List<CloudImageEntry>
            {
                MakeEntry("acc1", "A.jpg"),
                MakeEntry("acc1", "B.jpg"),
                MakeEntry("acc1", "C.jpg")
            };

            var index = ImageIndex.Build([(config, entries)]);

            // Asking for more than available returns all available.
            var picked = index.PickMany(100);
            Assert.Equal(3, picked.Count);
        }
    }
}
