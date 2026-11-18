using Ripcord.Core.Sessions;

namespace Ripcord.Presentation.Halyard.Sessions;

/// <summary>
/// A session opened through the account rendezvous, wrapped so that it owns the machinery the rendezvous
/// leaves running.
///
/// <para>
/// The account route's whole control plane rides an association that was opened before the session existed,
/// and the cloud session it belongs to must stay joined for as long as the stream runs — leaving it early
/// ends the session the console joined, and the console tears the association down with it. This wrapper
/// holds both and disposes them, in the right order, with the stream, so <see cref="IStreamingSession"/>
/// keeps its promise and no caller has to know which route produced the session it holds.
/// </para>
/// </summary>
internal sealed class AccountRouteSession : IStreamingSession
{
    private readonly IStreamingSession _inner;
    private readonly IAsyncDisposable? _association;

    public AccountRouteSession(IStreamingSession inner, IAsyncDisposable? association)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _association = association;
    }

    public Task<SessionHandshakeResult> ConnectAsync(SessionConfig config, CancellationToken cancellationToken)
        => _inner.ConnectAsync(config, cancellationToken);

    public IObservable<EncodedVideoFrame> VideoFrames => _inner.VideoFrames;

    public IObservable<EncodedAudioFrame> AudioFrames => _inner.AudioFrames;

    public IObservable<SessionStatistics> Statistics => _inner.Statistics;

    public ISessionInputSink InputSink => _inner.InputSink;

    public IBandwidthController BandwidthController => _inner.BandwidthController;

    public SessionState State => _inner.State;

    public string? EndReason => _inner.EndReason;

    public SessionEndKind EndKind => _inner.EndKind;

    public double? MillisecondsSinceConsoleActivity => _inner.MillisecondsSinceConsoleActivity;

    public bool RestConsoleOnDisconnect
    {
        get => _inner.RestConsoleOnDisconnect;
        set => _inner.RestConsoleOnDisconnect = value;
    }

    public void RequestKeyFrame() => _inner.RequestKeyFrame();

    /// <summary>
    /// Session first, then the association it rode on — that order matters. The session's own teardown speaks
    /// to the console over the association (the rest-on-disconnect frame among other things), so releasing the
    /// association first would silently drop the goodbye and leave the console believing a session is still
    /// live.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_association is not null)
            {
                await _association.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
