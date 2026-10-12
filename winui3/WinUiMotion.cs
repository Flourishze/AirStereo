using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace AirStereo;

/// <summary>Presentation-only compositor effects. No new polling or audio work.</summary>
internal sealed class WinUiMotion : IDisposable
{
    private readonly UISettings preferences = new();
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly Dictionary<FrameworkElement, Visual> surfaces = new();
    private readonly Dictionary<FrameworkElement, Vector3> baseOffsets = new();
    private readonly Dictionary<FrameworkElement, (bool Requested, bool Running)> rotations = new();
    private bool disposed;

    internal WinUiMotion() => preferences.AnimationsEnabledChanged += OnAnimationsChanged;

    private bool Enabled => !disposed && preferences.AnimationsEnabled;

    internal void Enter(FrameworkElement element, float distance = 10)
    {
        Reset(element);
        if (!Enabled) return;
        Visual visual = Surface(element);
        var compositor = visual.Compositor;
        using var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1), new Vector2(0.3f, 1));
        using var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(1, 1, easing);
        opacity.Duration = TimeSpan.FromMilliseconds(180);
        using var translation = compositor.CreateVector3KeyFrameAnimation();
        Vector3 offset = BaseOffset(element, visual);
        translation.InsertKeyFrame(0, offset + new Vector3(0, distance, 0));
        translation.InsertKeyFrame(1, offset, easing);
        translation.Duration = opacity.Duration;
        visual.StartAnimation("Opacity", opacity);
        // Older Windows App SDK projections do not expose Visual.Translation
        // as a managed property. Offset is the compatible composition property
        // and keeps the effect presentation-only.
        visual.StartAnimation("Offset", translation);
    }

    internal int Exit(FrameworkElement element)
    {
        Reset(element);
        if (!Enabled) return 0;
        Visual visual = Surface(element);
        using var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 1);
        animation.InsertKeyFrame(1, 0);
        animation.Duration = TimeSpan.FromMilliseconds(100);
        visual.StartAnimation("Opacity", animation);
        return 100;
    }

    internal void Pulse(FrameworkElement element)
    {
        if (disposed) return;
        Visual visual = Surface(element);
        // Layout owns Offset. Repeated clicks must not restore a position cached
        // before layout or before a button's enabled state changed.
        visual.StopAnimation("Scale");
        visual.Scale = Vector3.One;
        if (!Enabled) return;
        visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        using var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, Vector3.One);
        animation.InsertKeyFrame(0.45f, new Vector3(1.15f, 1.15f, 1));
        animation.InsertKeyFrame(1, Vector3.One);
        animation.Duration = TimeSpan.FromMilliseconds(180);
        visual.StartAnimation("Scale", animation);
    }

    internal void Spin(FrameworkElement element, bool requested)
    {
        if (disposed) return;
        bool running = requested && Enabled;
        bool wasRunning = rotations.TryGetValue(element, out var state) && state.Running;
        rotations[element] = (requested, running);
        Visual visual = Surface(element);
        visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        if (running == wasRunning) return;
        visual.StopAnimation("RotationAngleInDegrees");
        visual.RotationAngleInDegrees = 0;
        if (!running) return;
        using var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        using var linear = visual.Compositor.CreateLinearEasingFunction();
        animation.InsertKeyFrame(0, 0, linear);
        animation.InsertKeyFrame(1, 360, linear);
        animation.Duration = TimeSpan.FromMilliseconds(900);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("RotationAngleInDegrees", animation);
    }

    internal void Reset(FrameworkElement element)
    {
        if (!surfaces.TryGetValue(element, out Visual? visual)) return;
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Offset");
        visual.StopAnimation("Scale");
        visual.Opacity = 1;
        if (baseOffsets.TryGetValue(element, out Vector3 offset)) visual.Offset = offset;
        visual.Scale = Vector3.One;
    }

    private Visual Surface(FrameworkElement element)
    {
        if (!surfaces.TryGetValue(element, out Visual? visual))
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            visual = ElementCompositionPreview.GetElementVisual(element);
            surfaces.Add(element, visual);
            baseOffsets[element] = visual.Offset;
        }
        return visual;
    }

    private Vector3 BaseOffset(FrameworkElement element, Visual visual) =>
        baseOffsets.TryGetValue(element, out Vector3 offset) ? offset : (baseOffsets[element] = visual.Offset);

    private void OnAnimationsChanged(UISettings sender, object args) => dispatcher.TryEnqueue(() =>
    {
        if (disposed) return;
        if (!Enabled) foreach (var element in surfaces.Keys.ToArray()) Reset(element);
        foreach (var pair in rotations.ToArray()) Spin(pair.Key, pair.Value.Requested);
    });

    public void Dispose()
    {
        if (disposed) return;
        preferences.AnimationsEnabledChanged -= OnAnimationsChanged;
        foreach (var element in surfaces.Keys.ToArray()) Reset(element);
        foreach (var element in rotations.Keys.ToArray()) Spin(element, false);
        rotations.Clear();
        surfaces.Clear();
        baseOffsets.Clear();
        disposed = true;
    }
}
