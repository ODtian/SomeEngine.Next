using System.Globalization;
using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Elements.SliderFunctions;
using static SomeEngine.Elements.TextFunctions;
using static SomeEngine.Elements.ValueFunctions;
using static SomeEngine.Layout.FlexFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

internal static partial class ValueChecks
{
    internal static void RunVolume()
    {
        var game = new VolumeChangeSystem();
        using var frames = new ValueFrames(text: true, game);
        World world = frames.World;
        Entity settings = world.Create(new AudioSettings { Volume = 0.25f });
        Entity root = Views.Views.Spawn(world, frames.Canvas, new VolumeExample(settings));
        frames.Frame();
        VolumeDisplay display = world.ReadComponentData<VolumeDisplay>(root);
        AssertVolume(world, display, 0.25f, "0.25");
        Entity sliderThumb = FindSurface(world, display.Slider, new Size(12, 12));
        Entity sliderFace = FindSurface(world, display.Slider, new Size(160, 4));
        Entity knobPoint = FindSurface(world, display.Knob, new Size(6, 6));
        Entity knobFace = FindSurface(world, display.Knob, new Size(48, 48));
        Rect originalPoint = world.ReadComponentData<LayoutRect>(knobPoint).Value;

        game.ChangeVolume(0.5f);
        frames.Frame();
        Require(world.ReadComponentData<VolumeDisplay>(root) == display, "Updating source data retains the three declared nodes.");
        AssertVolume(world, display, 0.5f, "0.50");
        Require(Near(world.ReadComponentData<LayoutRect>(sliderThumb).Value.X, world.ReadComponentData<LayoutRect>(sliderFace).Value.X + 74),
            "The slider thumb moves to the middle of its visible track after a game ref write.");
        Rect indicator = world.ReadComponentData<LayoutRect>(knobPoint).Value;
        Rect face = world.ReadComponentData<LayoutRect>(knobFace).Value;
        Require(indicator != originalPoint && Near(indicator.X, face.X + 21) && Near(indicator.Y, face.Y + 7),
            "The user's circular indicator updates from the same game ref write.");

        Rect thumb = world.ReadComponentData<LayoutRect>(sliderThumb).Value;
        var start = new Vector2(thumb.X + 6, thumb.Y + 6);
        Pointer.Move(world, frames.Canvas, start);
        Pointer.Press(world, frames.Canvas, PointerButton.Primary);
        Pointer.Move(world, frames.Canvas, start + new Vector2(37, 0));
        Pointer.Release(world, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Require(Near(world.ReadComponentData<AudioSettings>(settings).Volume, 0.75f),
            "Dragging the slider saves its edit back into AudioSettings.");
        AssertVolume(world, display, 0.75f, "0.75");
        Require(Near(world.ReadComponentData<LayoutRect>(sliderThumb).Value.X, world.ReadComponentData<LayoutRect>(sliderFace).Value.X + 111),
            "The slider displays its accepted drag value.");
        Require(world.ReadComponentData<LayoutRect>(knobPoint).Value != indicator,
            "The user's indicator moves after the slider's accepted edit.");

        ulong textVersion = world.ComponentDataWriteVersion<Text>(display.Text);
        ulong contentVersion = world.ComponentDataWriteVersion<TextContent>(display.Text);
        ulong shapeVersion = world.ComponentDataWriteVersion<ShapedText>(display.Text);
        ulong layoutVersion = world.ComponentDataWriteVersion<LayoutRect>(display.Text);
        game.ChangeVolume(0.754f);
        frames.Frame();
        AssertVolume(world, display, 0.754f, "0.75");
        Require(world.ComponentDataWriteVersion<Text>(display.Text) == textVersion &&
            world.ComponentDataWriteVersion<TextContent>(display.Text) == contentVersion &&
            world.ComponentDataWriteVersion<ShapedText>(display.Text) == shapeVersion &&
            world.ComponentDataWriteVersion<LayoutRect>(display.Text) == layoutVersion,
            "A different volume with the same displayed characters preserves the text, content, shape, and layout versions.");
    }

    private static void AssertVolume(World world, VolumeDisplay display, float value, string text)
    {
        Require(Near(world.ReadComponentData<Slider>(display.Slider).Value, value) &&
            Near(world.ReadComponentData<Rewrites.Knob>(display.Knob).Value, value) &&
            world.ReadComponentData<TextContent>(display.Text).Value == text,
            "The slider, user's knob, and displayed text reflect the same volume.");
    }

    private static Entity FindSurface(World world, Entity root, Size size)
    {
        foreach (Entity child in ElementChildren.Read(world, root))
        {
            if (world.Has<Surface>(child) && world.Has<LayoutRect>(child))
            {
                Rect rect = world.ReadComponentData<LayoutRect>(child).Value;
                if (Near(rect.Width, size.Width) && Near(rect.Height, size.Height))
                    return child;
            }
            Entity match = FindSurface(world, child, size);
            if (match != Entity.Null)
                return match;
        }
        return Entity.Null;
    }

    private readonly record struct VolumeExample(Entity Settings) : IView, IComponent
    {
        public BuildTask Build()
        {
            float volume = Scope.Read<AudioSettings>(Settings).Volume;
            using (Row(8).Content())
            {
                Entity slider = Slider(volume);
                if (slider.Edited(out float sliderValue))
                    Scope.Write<AudioSettings>(Settings).Volume = sliderValue;
                Entity knob = Rewrites.KnobFunctions.Knob(volume);
                if (knob.Edited(out float knobValue))
                    Scope.Write<AudioSettings>(Settings).Volume = knobValue;
                Entity text = Text(volume.ToString("F2", CultureInfo.InvariantCulture));
                Scope.Write<VolumeDisplay>(Scope.Entity) = new VolumeDisplay(slider, knob, text);
            }
            return default;
        }
    }

    private readonly record struct VolumeDisplay(Entity Slider, Entity Knob, Entity Text) : IComponent;

    private sealed class VolumeChangeSystem : ISystem<ImmediateSystemContext>
    {
        private Query _settings;
        private float _volume;
        private bool _hasChange;

        internal void ChangeVolume(float value)
        {
            _volume = value;
            _hasChange = true;
        }

        public void OnCreate(ref ImmediateSystemContext context) =>
            _settings = context.World.Query(new QueryDescription().WithStored<AudioSettings>());

        public void OnUpdate(ref ImmediateSystemContext context)
        {
            if (!_hasChange)
                return;
            using (QueryScope rows = _settings.Open())
                foreach (QueryChunk chunk in rows)
                    foreach (ref AudioSettings settings in chunk.Write<AudioSettings>()) settings.Volume = _volume;
            _hasChange = false;
        }

        public void OnDestroy(ref ImmediateSystemContext context) { }
    }
}
