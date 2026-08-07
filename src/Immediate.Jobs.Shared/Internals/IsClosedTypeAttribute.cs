// TODO: Remove with Meziantou.Polyfill update
#if !NET11_0_OR_GREATER

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace System.Runtime.CompilerServices;
#pragma warning restore IDE0130 // Namespace does not match folder structure

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class IsClosedTypeAttribute : Attribute { }

#endif
