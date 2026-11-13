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
        RetainSession(global, session, code);
    }

    public IGlobalSession Global { get; }
    public ISession Session { get; }
    public IComponentType Code { get; }

    /// <summary>Keeps a component's native session and global session alive through its COM wrapper's finalizer.</summary>
    public static void RetainSession(IGlobalSession global, ISession session, IComponentType component)
    {
        SessionReference reference = s_sessions.GetValue(session, value => new SessionReference(global, value));
        s_components.GetValue(component, _ => reference);
    }

    // Components borrow their session's Linkage. Associate the native references
    // with every COM wrapper so it follows that wrapper across GC generations.
    // SafeHandle releases the session, then its global, after the last wrapper's ordinary finalizer.
    private sealed unsafe class SessionReference : SafeHandle
    {
        private readonly nint _globalSession;

        internal SessionReference(IGlobalSession global, ISession session) : base(IntPtr.Zero, ownsHandle: true)
        {
            _globalSession = (nint)ComInterfaceMarshaller<IGlobalSession>.ConvertToUnmanaged(global);
            SetHandle((nint)ComInterfaceMarshaller<ISession>.ConvertToUnmanaged(session));
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            Marshal.Release(handle);
            Marshal.Release(_globalSession);
            return true;
        }
    }
}
