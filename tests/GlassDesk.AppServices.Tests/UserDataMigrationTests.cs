using GlassDesk.AppServices;
using GlassDesk.Platform.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class UserDataMigrationTests
{
    [Fact]
    public void MigrationCopiesEveryFileWithVerificationAndKeepsLegacyRollbackCopy()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "install", "Data");
            var destination = Path.Combine(root, "profile", "LG Dsk", "Data");
            Directory.CreateDirectory(Path.Combine(source, "icons"));
            File.WriteAllText(Path.Combine(source, "settings.json"), "{\"schemaVersion\":1}");
            File.WriteAllBytes(Path.Combine(source, "icons", "custom-test.png"), [0, 9, 42, 255]);

            var result = UserDataMigration.MigrateLegacyData(source, destination);

            Assert.Equal(UserDataMigrationStatus.Migrated, result.Status);
            Assert.Equal(2, result.FilesCopied);
            Assert.Equal(File.ReadAllText(Path.Combine(source, "settings.json")),
                File.ReadAllText(Path.Combine(destination, "settings.json")));
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, "icons", "custom-test.png")),
                File.ReadAllBytes(Path.Combine(destination, "icons", "custom-test.png")));
            Assert.True(File.Exists(Path.Combine(source, "settings.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExistingDestinationStopsWithoutReplacingEitherSide()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "install", "Data");
            var destination = Path.Combine(root, "profile", "LG Dsk", "Data");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(source, "apps.json"), "legacy");
            File.WriteAllText(Path.Combine(destination, "apps.json"), "newer");

            var result = UserDataMigration.MigrateLegacyData(source, destination);

            Assert.Equal(UserDataMigrationStatus.Conflict, result.Status);
            Assert.Equal("legacy", File.ReadAllText(Path.Combine(source, "apps.json")));
            Assert.Equal("newer", File.ReadAllText(Path.Combine(destination, "apps.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ActiveLegacyRuntimeLeaseBlocksMigration()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "install", "Data");
            var destination = Path.Combine(root, "profile", "LG Dsk", "Data");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "settings.json"), "legacy");
            Assert.True(UserDataLease.TryAcquireShared(source, out var lease));
            using (lease)
            {
                var result = UserDataMigration.MigrateLegacyData(source, destination);
                Assert.Equal(UserDataMigrationStatus.SourceInUse, result.Status);
                Assert.False(Directory.Exists(destination));
                Assert.Equal("legacy", File.ReadAllText(Path.Combine(source, "settings.json")));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SuccessfulMigrationIsIdempotentAndRetainsRollbackCopy()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "install", "Data");
            var destination = Path.Combine(root, "profile", "LG Dsk", "Data");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "settings.json"), "legacy");

            var first = UserDataMigration.MigrateLegacyData(source, destination);
            var second = UserDataMigration.MigrateLegacyData(source, destination);

            Assert.Equal(UserDataMigrationStatus.Migrated, first.Status);
            Assert.Equal(UserDataMigrationStatus.AlreadyAtDestination, second.Status);
            Assert.Equal(first.FilesCopied, second.FilesCopied);
            Assert.Equal("legacy", File.ReadAllText(Path.Combine(source, "settings.json")));
            Assert.Equal("legacy", File.ReadAllText(Path.Combine(destination, "settings.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EmptyLegacyDirectoryDoesNotBlockExistingDestination()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "install", "Data");
            var destination = Path.Combine(root, "profile", "LG Dsk", "Data");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "settings.json"), "current");

            var result = UserDataMigration.MigrateLegacyData(source, destination);

            Assert.Equal(UserDataMigrationStatus.NoLegacyData, result.Status);
            Assert.Equal("current", File.ReadAllText(Path.Combine(destination, "settings.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OverlappingDirectoriesAreRejectedBeforeCopy()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "install", "Data");
            var destination = Path.Combine(source, "nested");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "keep.txt"), "safe");

            var result = UserDataMigration.MigrateLegacyData(source, destination);

            Assert.Equal(UserDataMigrationStatus.Conflict, result.Status);
            Assert.False(Directory.Exists(destination));
            Assert.Equal("safe", File.ReadAllText(Path.Combine(source, "keep.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "LG-Dsk-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
