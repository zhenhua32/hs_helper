using HsHelper.Core;
using HsHelper.Storage;

namespace HsHelper.App;

public partial class MainWindow
{
    private GameSnapshot? _liveFrame;
    private DeckTrackingEnricher? _trackingFrames;
    private Task<DeckTrackingEnricher> CreateTrackingFramesAsync(string? build)
        => DeckTrackingEnricher.CreateAsync(_catalog, _sourceDeckVersion, build);
}
