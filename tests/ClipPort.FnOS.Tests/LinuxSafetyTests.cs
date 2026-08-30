using ClipPort.FnOS.Persistence;
using ClipPort.Models;
using ClipPort.Services;

namespace ClipPort.FnOS.Tests;

public sealed class LinuxSafetyTests
{
    [Fact]
    public void PartialJournalCleansOnlyExactRecordedClipPortFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "clipport-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string tracked = Path.Combine(root, "tracked.clipport-partial");
            string untracked = Path.Combine(root, "untracked.clipport-partial");
            File.WriteAllText(tracked, "partial");
            File.WriteAllText(untracked, "keep");
            var journal = new JsonPartialFileJournal(root);
            journal.Track(tracked);

            new JsonPartialFileJournal(root).CleanupTrackedFiles();

            Assert.False(File.Exists(tracked));
            Assert.True(File.Exists(untracked));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LinuxPathSemanticsAreCaseSensitive()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.False(PathSemantics.Comparer.Equals("/volume/Media", "/volume/media"));
    }

    [Fact]
    public void SourceAndDestinationAreRejectedInEitherOverlapDirection()
    {
        string parent = Path.Combine(Path.GetTempPath(), "clipport-overlap-root");
        string child = Path.Combine(parent, "child");

        Assert.False(PathSafety.TryValidateSourceAndDestination(parent, child, out PathValidationError childError));
        Assert.Equal(PathValidationError.DestinationIsInsideSource, childError);

        Assert.False(PathSafety.TryValidateSourceAndDestination(child, parent, out PathValidationError parentError));
        Assert.Equal(PathValidationError.SourceIsInsideDestination, parentError);
    }
}
