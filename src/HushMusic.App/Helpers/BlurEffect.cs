using System.Runtime.InteropServices;
using Windows.Graphics.Effects;
using WinRT;

namespace HushMusic.App.Helpers;

/// <summary>
/// A Gaussian blur for Composition effect brushes, without Win2D. Composition takes any <see cref="IGraphicsEffect"/>
/// that also describes itself to Direct2D through <c>IGraphicsEffectD2D1Interop</c> (the effect's CLSID, its property
/// values and its sources); Win2D's effect classes are exactly that. This is the smallest such object, for D2D1's
/// Gaussian blur only, written as a plain native COM object.
/// </summary>
/// <remarks>
/// Composition reads the description once, when the effect factory is made, and the factory is what gets reused. The
/// objects are tiny and few (one per kind of blur), so they are never freed and reference counting is a no-op.
/// </remarks>
internal static class BlurEffect
{
    private const int S_OK = 0;
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int E_FAIL = unchecked((int)0x80004005);

    // GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT: the value goes to the D2D property as it is.
    private const int MappingDirect = 1;

    private static readonly Guid GaussianBlurClsid = new("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5");
    private static readonly Guid IUnknownIid = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IInspectableIid = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
    private static readonly Guid IAgileObjectIid = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    private static readonly Guid IGraphicsEffectIid = new("CB51C0CE-8FE6-4636-B202-861FAA07D8F3");
    private static readonly Guid IGraphicsEffectSourceIid = new("2D8F9DDC-4339-4EB9-9216-F9DEB75658A2");
    private static readonly Guid IGraphicsEffectD2D1InteropIid = new("2FC57384-A068-44D7-A331-30982FCF7177");
    private static readonly Guid IPropertyValueIid = new("4BD682DD-7554-40E9-9A9B-82654EDE7E62");

    // D2D1_GAUSSIANBLUR_PROP_*: standard deviation, optimization, border mode (named as Win2D names them).
    private static readonly string[] PropertyNames = ["BlurAmount", "Optimization", "BorderMode"];

    private static readonly Lock Gate = new();
    private static readonly Dictionary<IntPtr, Description> Effects = [];

    // The delegates behind the vtables; static, so they live as long as the vtables do.
    private static readonly QueryInterfaceFn QueryEffectFn = QueryEffect;
    private static readonly QueryInterfaceFn QueryInteropFn = QueryInterop;
    private static readonly RefCountFn KeepAliveFn = _ => 1;
    private static readonly GetIidsFn GetIidsImpl = GetIids;
    private static readonly GetPointerFn GetNullStringFn = GetNullString;
    private static readonly GetIntFn GetTrustLevelFn = GetTrustLevel;
    private static readonly SetPointerFn PutNameFn = (_, _) => S_OK;
    private static readonly GetGuidFn GetEffectIdFn = GetEffectId;
    private static readonly GetMappingFn GetNamedPropertyMappingFn = GetNamedPropertyMapping;
    private static readonly GetCountFn GetPropertyCountFn = GetPropertyCount;
    private static readonly GetAtFn GetPropertyFn = GetProperty;
    private static readonly GetAtFn GetSourceFn = GetSource;
    private static readonly GetCountFn GetSourceCountFn = GetSourceCount;

    // IGraphicsEffect (also answers for IUnknown, IInspectable and IGraphicsEffectSource), then IGraphicsEffectD2D1Interop.
    private static readonly IntPtr EffectVtable = Vtable(
        QueryEffectFn, KeepAliveFn, KeepAliveFn, GetIidsImpl, GetNullStringFn, GetTrustLevelFn, GetNullStringFn, PutNameFn);

    private static readonly IntPtr InteropVtable = Vtable(
        QueryInteropFn, KeepAliveFn, KeepAliveFn, GetEffectIdFn, GetNamedPropertyMappingFn, GetPropertyCountFn, GetPropertyFn, GetSourceFn, GetSourceCountFn);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint RefCountFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetIidsFn(IntPtr self, out uint count, out IntPtr iids);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPointerFn(IntPtr self, out IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPointerFn(IntPtr self, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetIntFn(IntPtr self, out int value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetGuidFn(IntPtr self, out Guid value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMappingFn(IntPtr self, IntPtr name, out uint index, out int mapping);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCountFn(IntPtr self, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAtFn(IntPtr self, uint index, out IntPtr value);

    /// <summary>
    /// A blur of <paramref name="source"/> (e.g. a <c>CompositionEffectSourceParameter</c>) by
    /// <paramref name="standardDeviation"/> pixels. Edges are hard: the blur doesn't fade out at the input's border.
    /// </summary>
    public static IGraphicsEffect Create(IGraphicsEffectSource source, float standardDeviation)
    {
        // D2D1_GAUSSIANBLUR_OPTIMIZATION_BALANCED, D2D1_BORDER_MODE_HARD.
        object[] properties = [standardDeviation, 1u, 1u];
        IntPtr[] sources = [MarshalInterface<IGraphicsEffectSource>.FromManaged(source)];

        // Two vtable pointers: the object as IGraphicsEffect, and right after it the same object as the interop.
        var instance = Marshal.AllocHGlobal(2 * IntPtr.Size);
        Marshal.WriteIntPtr(instance, EffectVtable);
        Marshal.WriteIntPtr(instance, IntPtr.Size, InteropVtable);
        lock (Gate)
        {
            Effects[instance] = new Description(GaussianBlurClsid, properties, sources);
        }

        return MarshalInterface<IGraphicsEffect>.FromAbi(instance);
    }

    private static IntPtr Vtable(params Delegate[] methods)
    {
        var table = Marshal.AllocHGlobal(methods.Length * IntPtr.Size);
        for (var i = 0; i < methods.Length; i++)
        {
            Marshal.WriteIntPtr(table, i * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(methods[i]));
        }

        return table;
    }

    private static int QueryEffect(IntPtr self, ref Guid iid, out IntPtr result) => Query(self, iid, out result);

    private static int QueryInterop(IntPtr self, ref Guid iid, out IntPtr result) => Query(self - IntPtr.Size, iid, out result);

    private static int Query(IntPtr instance, Guid iid, out IntPtr result)
    {
        if (iid == IUnknownIid || iid == IInspectableIid || iid == IGraphicsEffectIid || iid == IGraphicsEffectSourceIid || iid == IAgileObjectIid)
        {
            result = instance;
            return S_OK;
        }

        if (iid == IGraphicsEffectD2D1InteropIid)
        {
            result = instance + IntPtr.Size;
            return S_OK;
        }

        result = IntPtr.Zero;
        return E_NOINTERFACE;
    }

    private static int GetIids(IntPtr self, out uint count, out IntPtr iids)
    {
        count = 0;
        iids = IntPtr.Zero;
        return S_OK;
    }

    // Runtime class name and effect name: an empty string (a null HSTRING).
    private static int GetNullString(IntPtr self, out IntPtr value)
    {
        value = IntPtr.Zero;
        return S_OK;
    }

    private static int GetTrustLevel(IntPtr self, out int value)
    {
        value = 0;
        return S_OK;
    }

    private static int GetEffectId(IntPtr self, out Guid value)
    {
        value = Find(self)?.Clsid ?? Guid.Empty;
        return value == Guid.Empty ? E_FAIL : S_OK;
    }

    private static int GetNamedPropertyMapping(IntPtr self, IntPtr name, out uint index, out int mapping)
    {
        index = 0;
        mapping = 0;
        var position = Array.IndexOf(PropertyNames, Marshal.PtrToStringUni(name));
        if (position < 0)
        {
            return E_INVALIDARG;
        }

        index = (uint)position;
        mapping = MappingDirect;
        return S_OK;
    }

    private static int GetPropertyCount(IntPtr self, out uint count)
    {
        count = (uint)(Find(self)?.Properties.Length ?? 0);
        return S_OK;
    }

    private static int GetProperty(IntPtr self, uint index, out IntPtr value)
    {
        value = IntPtr.Zero;
        if (Find(self) is not { } effect || index >= effect.Properties.Length)
        {
            return E_INVALIDARG;
        }

        try
        {
            // A boxed float or uint goes out as an IReference<T>, which is also the IPropertyValue D2D wants.
            var boxed = MarshalInspectable<object>.FromManaged(effect.Properties[index]);
            try
            {
                return Marshal.QueryInterface(boxed, in IPropertyValueIid, out value);
            }
            finally
            {
                Marshal.Release(boxed);
            }
        }
        catch (Exception ex)
        {
            return ex.HResult != 0 ? ex.HResult : E_FAIL;
        }
    }

    private static int GetSource(IntPtr self, uint index, out IntPtr value)
    {
        value = IntPtr.Zero;
        if (Find(self) is not { } effect || index >= effect.Sources.Length)
        {
            return E_INVALIDARG;
        }

        value = effect.Sources[index];
        Marshal.AddRef(value);
        return S_OK;
    }

    private static int GetSourceCount(IntPtr self, out uint count)
    {
        count = (uint)(Find(self)?.Sources.Length ?? 0);
        return S_OK;
    }

    // The interop methods are called on the second vtable pointer.
    private static Description? Find(IntPtr interop)
    {
        lock (Gate)
        {
            return Effects.GetValueOrDefault(interop - IntPtr.Size);
        }
    }

    private sealed record Description(Guid Clsid, object[] Properties, IntPtr[] Sources);
}
