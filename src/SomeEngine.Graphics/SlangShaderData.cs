using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using SlangShaderSharp;

namespace SomeEngine.Graphics;

/// <summary>Retains compiled Slang code together with the session that owns its reflection.</summary>
public sealed class SlangShaderData
{
    private static readonly ConditionalWeakTable<ISession, SessionReference> s_sessions = new();
    private static readonly ConditionalWeakTable<IComponentType, SessionReference> s_components = new();

    public SlangShaderData(IGlobalSession global, ISession session, IComponentType code)
    {
        Global = global;
        Session = session;
        Code = code;
        RetainSession(session, code);
    }

    public IGlobalSession Global { get; }
    public ISession Session { get; }
    public IComponentType Code { get; }

    /// <summary>Keeps a component's native session alive through its COM wrapper's finalizer.</summary>
    public static void RetainSession(ISession session, IComponentType component)
    {
        SessionReference reference = s_sessions.GetValue(session, static value => new SessionReference(value));
        s_components.GetValue(component, _ => reference);
    }

    // Components borrow their session's Linkage. Associate the native reference
    // with every COM wrapper so it follows that wrapper across GC generations.
    // SafeHandle releases it after the last wrapper's ordinary finalizer.
    private sealed unsafe class SessionReference : SafeHandle
    {
        internal SessionReference(ISession session) : base(IntPtr.Zero, ownsHandle: true) =>
            SetHandle((nint)ComInterfaceMarshaller<ISession>.ConvertToUnmanaged(session));

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            Marshal.Release(handle);
            return true;
        }
    }
}
