using Ripcord.Core.Input;

namespace Ripcord.Core.Sessions;

/// <summary>
/// Wires a session to its consumers: encoded video/audio frames into a decode pipeline, and neutral
/// controller frames from a source into the session's input sink. This is the composition glue the
/// app performs once per session - kept in Core (platform-neutral) so it serves every backend.
/// </summary>
public static class SessionMediaBridge
{
    /// <summary>Route the session's encoded frames into <paramref name="pipeline"/>. Dispose to unsubscribe.</summary>
    public static IDisposable ConnectVideoAndAudio(IStreamingSession session, IVideoDecodePipeline pipeline)
    {
        IDisposable video = session.VideoFrames.Subscribe(new Sink<EncodedVideoFrame>(pipeline.SubmitEncodedVideo));
        IDisposable audio = session.AudioFrames.Subscribe(new Sink<EncodedAudioFrame>(pipeline.SubmitEncodedAudio));
        return new Composite(video, audio);
    }

    /// <summary>Route controller frames from <paramref name="controllerStates"/> into the session's input sink.</summary>
    public static IDisposable ConnectInput(IStreamingSession session, IObservable<ControllerStateFrame> controllerStates)
        => controllerStates.Subscribe(new Sink<ControllerStateFrame>(session.InputSink.SubmitControllerState));

    private sealed class Sink<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class Composite(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (IDisposable item in items)
            {
                item.Dispose();
            }
        }
    }
}
