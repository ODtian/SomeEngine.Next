using System.Numerics;

namespace SomeEngine.Runtime;

internal sealed class RuntimeCamera
{
    private const float MaximumUpAlignment = 0.995f;
    private Vector3 _forward;
    private readonly Vector3 _worldUp;

    internal RuntimeCamera(Vector3 position, Vector3 target, Vector3 up)
    {
        if (!IsFinite(position) || !IsFinite(target) || !IsFinite(up))
            throw new ArgumentException("Runtime camera vectors must be finite.");
        Vector3 forward = target - position;
        if (forward.LengthSquared() <= 1.0e-10f || up.LengthSquared() <= 1.0e-10f)
            throw new ArgumentException("Runtime camera direction and up vectors must be non-zero.");

        Position = position;
        _forward = Vector3.Normalize(forward);
        _worldUp = Vector3.Normalize(up);
        if (MathF.Abs(Vector3.Dot(_forward, _worldUp)) >= MaximumUpAlignment)
            throw new ArgumentException("Runtime camera direction cannot be parallel to its up vector.");
    }

    internal Vector3 Position { get; private set; }

    internal Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Position + _forward, _worldUp);

    internal void Update(
        RuntimeInput input,
        float deltaSeconds,
        bool captureKeyboard,
        bool captureMouse)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));

        float dt = Math.Min(deltaSeconds, 1.0f / 20.0f);
        if (!captureKeyboard)
        {
            float speed = IsShiftDown(input) ? 18.0f : 6.0f;
            Vector3 movement = Vector3.Zero;
            if (input.IsKeyDown(RuntimeInput.KeyW)) movement.Z += 1.0f;
            if (input.IsKeyDown(RuntimeInput.KeyS)) movement.Z -= 1.0f;
            if (input.IsKeyDown(RuntimeInput.KeyD)) movement.X += 1.0f;
            if (input.IsKeyDown(RuntimeInput.KeyA)) movement.X -= 1.0f;
            if (input.IsKeyDown(RuntimeInput.KeySpace)) movement.Y += 1.0f;
            if (IsControlDown(input)) movement.Y -= 1.0f;
            if (movement != Vector3.Zero)
            {
                movement = Vector3.Normalize(movement);
                Vector3 right = Right;
                Position += (right * movement.X + _worldUp * movement.Y + _forward * movement.Z)
                    * (speed * dt);
            }
        }

        if (!captureMouse && input.IsMouseButtonDown(NativeMouseButton.Right))
            Rotate(input.MouseDelta * new Vector2(0.0035f, -0.0035f));
    }

    private Vector3 Right => Vector3.Normalize(Vector3.Cross(_worldUp, _forward));

    private void Rotate(Vector2 radians)
    {
        if (radians == Vector2.Zero)
            return;
        Quaternion yaw = Quaternion.CreateFromAxisAngle(_worldUp, radians.X);
        Vector3 yawed = Vector3.Normalize(Vector3.Transform(_forward, yaw));
        Vector3 right = Vector3.Normalize(Vector3.Cross(_worldUp, yawed));
        Quaternion pitch = Quaternion.CreateFromAxisAngle(right, radians.Y);
        Vector3 candidate = Vector3.Normalize(Vector3.Transform(yawed, pitch));
        _forward = MathF.Abs(Vector3.Dot(candidate, _worldUp)) < MaximumUpAlignment
            ? candidate
            : yawed;
    }

    private static bool IsShiftDown(RuntimeInput input) =>
        input.IsKeyDown(RuntimeInput.KeyShift)
        || input.IsKeyDown(RuntimeInput.KeyLeftShift)
        || input.IsKeyDown(RuntimeInput.KeyRightShift);

    private static bool IsControlDown(RuntimeInput input) =>
        input.IsKeyDown(RuntimeInput.KeyControl)
        || input.IsKeyDown(RuntimeInput.KeyLeftControl)
        || input.IsKeyDown(RuntimeInput.KeyRightControl);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
