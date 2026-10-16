using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using SlangShaderSharp;

namespace SomeEngine.Graphics;

/// <summary>Retains compiled Slang code together with the session that owns its reflection.</summary>
public sealed class SlangShaderData
{
    private readonly SessionReference _sessionReference;

    public SlangShaderData(IGlobalSession global, ISession session, IComponentType code)
    {
        Global = global;
        Session = session;
        Code = code;
        _sessionReference = new SessionReference(session);
    }

    public IGlobalSession Global { get; }
    public ISession Session { get; }
    public IComponentType Code { get; }

    // ComponentType keeps a borrowed Linkage pointer. A native session reference
    // must also survive the ordinary COM wrapper finalizers when this data dies.
    // SafeHandle releases it in the subsequent critical-finalizer phase.
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
