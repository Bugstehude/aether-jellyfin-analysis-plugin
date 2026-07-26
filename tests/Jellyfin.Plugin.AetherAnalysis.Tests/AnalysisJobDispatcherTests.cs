using Jellyfin.Plugin.AetherAnalysis.Application;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

/// <summary>
/// Tests für die Auftrags-Warteschlange.
///
/// Sie war bis vor Kurzem UNBEGRENZT: wer auf dem Server Upload-Recht hat,
/// konnte beliebig viele jeweils minuten- bis stundenlange ffmpeg-Läufe
/// aufstauen und die Maschine über Stunden auslasten. Die Begrenzung ist
/// eingebaut — hier steht sie erstmals unter Test, denn eine Obergrenze, die
/// niemand prüft, ist eine Behauptung.
///
/// `Enqueue` fasst den Runner nie an (er wird erst in `ExecuteAsync` gebraucht,
/// und die läuft hier nicht), deshalb genügt `null!` an seiner Stelle. Ein
/// echter Runner bräuchte acht weitere Abhängigkeiten und würde nichts
/// zusätzlich absichern.
/// </summary>
public sealed class AnalysisJobDispatcherTests
{
    private const int QueueCapacity = 64;

    private static AnalysisJobDispatcher CreateDispatcher() =>
        new(runner: null!, NullLogger<AnalysisJobDispatcher>.Instance);

    [Fact]
    public void AcceptsAnItemAndReportsItAsQueued()
    {
        var dispatcher = CreateDispatcher();
        var status = dispatcher.Enqueue(Guid.NewGuid());

        Assert.NotNull(status);
        Assert.Equal(AnalysisJobState.Queued, status.State);
        Assert.Equal(0, status.Progress);
    }

    [Fact]
    public void AsksTwiceForTheSameItemWithoutQueueingItTwice()
    {
        // Sonst könnte ein Client denselben Knopf zwanzigmal drücken und
        // zwanzig identische Läufe erzeugen.
        var dispatcher = CreateDispatcher();
        var itemId = Guid.NewGuid();

        var first = dispatcher.Enqueue(itemId);
        var second = dispatcher.Enqueue(itemId);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Same(first, second);
    }

    [Fact]
    public void RejectsFurtherItemsOnceTheQueueIsFull()
    {
        var dispatcher = CreateDispatcher();
        for (var i = 0; i < QueueCapacity; i++)
        {
            Assert.NotNull(dispatcher.Enqueue(Guid.NewGuid()));
        }

        // Der 65. Auftrag wird abgelehnt — der Controller macht daraus ein 429
        // mit Retry-After, statt ihn still anzunehmen und nie zu liefern.
        Assert.Null(dispatcher.Enqueue(Guid.NewGuid()));
    }

    [Fact]
    public void DoesNotLeaveARejectedItemStuckInQueuedState()
    {
        // Der eigentlich heikle Teil: würde der abgelehnte Auftrag als "queued"
        // stehen bleiben, fragte der Client bis in alle Ewigkeit einen
        // Fortschritt ab, an dem niemand arbeitet — UND ein erneuter Versuch
        // desselben Items käme wegen der Dedupe-Regel nie mehr durch.
        var dispatcher = CreateDispatcher();
        for (var i = 0; i < QueueCapacity; i++)
        {
            dispatcher.Enqueue(Guid.NewGuid());
        }

        var rejected = Guid.NewGuid();
        Assert.Null(dispatcher.Enqueue(rejected));
        Assert.Null(dispatcher.GetStatus(rejected));
    }

    [Fact]
    public void ReportsNoStatusForAnItemNobodyAskedAbout()
    {
        Assert.Null(CreateDispatcher().GetStatus(Guid.NewGuid()));
    }
}
