// Mono 2.0 polyfills for compiler-generated attribute references.
//
// Unity 5.6.3 ships with Mono 2.0.50727 (.NET 3.5-equivalent). The C# 9
// compiler we use (LangVersion 9.0) emits attribute references that don't
// exist on this runtime. Defining them in the local assembly makes the
// compiler resolve them here instead of mscorlib — no IL change to the
// methods themselves; just makes the JIT able to load them.
//
// Without these, every `yield`-using method (e.g. Unity coroutines) emits
// a `[IteratorStateMachineAttribute]` reference that fails to resolve at
// type-load time → `TypeLoadException` → all Harmony patches in the
// assembly fail to install → 1000+ NullRefs from stock SF code paths
// where patches were expected to be active. Same goes for AsyncStateMachine
// (kept for safety though we don't use async).

using System;

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class IteratorStateMachineAttribute : Attribute
    {
        public Type StateMachineType { get; }
        public IteratorStateMachineAttribute(Type stateMachineType) { StateMachineType = stateMachineType; }
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class AsyncStateMachineAttribute : Attribute
    {
        public Type StateMachineType { get; }
        public AsyncStateMachineAttribute(Type stateMachineType) { StateMachineType = stateMachineType; }
    }
}
