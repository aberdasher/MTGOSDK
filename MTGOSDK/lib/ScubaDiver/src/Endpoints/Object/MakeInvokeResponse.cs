/** @file
  Copyright (c) 2021, Xappy.
  Copyright (c) 2024, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

using MTGOSDK.Core.Logging;
using MTGOSDK.Core.Reflection.Extensions;
using MTGOSDK.Core.Reflection.Types;
using MTGOSDK.Core.Remoting.Interop;
using MTGOSDK.Core.Remoting.Interop.Interactions;


namespace ScubaDiver;

public partial class Diver : IDisposable
{
  private static readonly TypeStub s_typeStub = new();

  // Endpoint-level method cache: uses string keys for safety
  // Key: (typeFullName, methodName, paramCount, paramTypeHash)
  private static readonly ConcurrentDictionary<(string, string, int, int), MethodInfo> 
    s_methodCache = new();

  private static int ComputeTypeHash(Type[] types)
  {
    if (types == null) return 0;
    int hash = 17;
    foreach (var t in types)
      hash = hash * 31 + (t?.FullName?.GetHashCode() ?? 0);
    return hash;
  }

  private byte[] MakeInvokeResponse()
  {
    var request = DeserializeRequest<InvocationRequest>();
    if (request == null)
      return QuickError("Missing or invalid request body");

    Log.Debug($"[Diver] Got /Invoke request: method={request.MethodName}, type={request.TypeFullName}, addr={request.ObjAddress:X}");

    object instance = null;
    Type dumpedObjType;
    if (request.ObjAddress == 0)
    {
      dumpedObjType = _runtime.ResolveType(request.TypeFullName);
    }
    else
    {
      if (!_runtime.TryGetPinnedObject(request.ObjAddress, out instance))
        return QuickError("Couldn't find object in pinned pool");

      dumpedObjType = instance.GetType();
    }

    int paramCount = request.Parameters?.Count ?? 0;
    object[] paramsArray = new object[paramCount];
    Type[] argumentTypes = new Type[paramCount];

    for (int i = 0; i < paramCount; i++)
    {
      paramsArray[i] = _runtime.ParseParameterObject(request.Parameters[i]);
      argumentTypes[i] = paramsArray[i]?.GetType() ?? typeof(object);
    }

    // Resolve generic type arguments UP FRONT. Generic methods such as
    // ITradeEscrow.Process<T>(T message) have open generic placeholders for their
    // parameter types until MakeGenericMethod is applied, so method resolution
    // must know the generic args BEFORE matching parameter types — otherwise the
    // concrete argument type never matches the 'T' parameter and the method is
    // reported as not found.
    Type[] genericArgs = null;
    if (request.GenericArgsTypeFullNames?.Length > 0)
    {
      genericArgs = new Type[request.GenericArgsTypeFullNames.Length];
      for (int i = 0; i < request.GenericArgsTypeFullNames.Length; i++)
      {
        genericArgs[i] = _runtime.ResolveType(request.GenericArgsTypeFullNames[i]);
        if (genericArgs[i] == null)
          return QuickError($"Failed to resolve generic type: {request.GenericArgsTypeFullNames[i]}");
      }
    }

    // Create cache key (fold generic args into the hash so generic and
    // non-generic resolutions for the same name/arity do not collide).
    var cacheKey = (dumpedObjType.FullName, request.MethodName, paramCount,
                    ComputeTypeHash(argumentTypes) * 31 + ComputeTypeHash(genericArgs));

    // Try cache first
    if (!s_methodCache.TryGetValue(cacheKey, out var method))
    {
      // Generic-aware resolver overload: filters to generic methods, applies
      // MakeGenericMethod(genericArgs), then matches parameter types. When
      // genericArgs is null this behaves identically to the non-generic lookup.
      method = dumpedObjType.GetMethodRecursive(
        request.MethodName,
        genericArgs,
        argumentTypes
      );

      if (method != null)
        s_methodCache[cacheKey] = method;
    }

    if (method == null)
      return QuickError($"Couldn't find method '{request.MethodName}' on type '{dumpedObjType.FullName}'");

    // (generics already applied by the generic-aware GetMethodRecursive above)
    // Marshal to MTGO's application UI dispatcher whenever the caller explicitly
    // requested it (ForceUIThread) — NOT only when the target is a DispatcherObject.
    // Many plain model-object methods (e.g. ITradeEscrow.Process) synchronously
    // trigger WPF UI updates downstream (TradeSceneViewModel -> DataGrid); those
    // must run on the UI thread or they throw thread-affinity errors and corrupt
    // the UI. STAThread.Execute dispatches onto Application.Current.Dispatcher.
    bool needsUIThread = request.ForceUIThread;

    // Start sub-activity for the actual reflection invocation
    using var activity = s_activitySource.StartActivity("MethodInvoke");
    if (activity != null)
    {
      activity.SetTag("method", request.MethodName);
      activity.SetTag("type", request.TypeFullName);
      activity.SetTag("addr", request.ObjAddress.ToString("X"));
    }

    object results;
    try
    {
      if (needsUIThread && !STAThread.IsDispatcherThread)
      {
        // ForceUIThread requested — run on MTGO's application UI dispatcher so any
        // downstream WPF UI updates happen on the correct thread.
        Log.Debug($"[Diver] Invoking {request.MethodName} on UI thread (ForceUIThread)");
        results = STAThread.Execute(() => method.Invoke(instance, paramsArray));
      }
      else
      {
        // Try direct execution first - most operations don't need UI thread
        results = method.Invoke(instance, paramsArray);
      }
    }
    catch (Exception e)
    {
      activity?.SetStatus(ActivityStatusCode.Error, e.Message);
      return QuickError($"Invocation caused exception: {e}");
    }

    ObjectOrRemoteAddress returnValue;
    if (method.ReturnType == typeof(void))
    {
      returnValue = ObjectOrRemoteAddress.Null;
    }
    else if (results == null)
    {
      returnValue = ObjectOrRemoteAddress.Null;
    }
    else if (results.GetType().IsPrimitiveEtc()
          || results.GetType().IsPrimitiveEtcArray())
    {
      returnValue = ObjectOrRemoteAddress.FromObj(results);
    }
    else
    {
      ulong resultsAddress = _runtime.PinObject(results);
      Type resultsType = results.GetType();
      int hashCode = results.GetHashCode();
      returnValue = ObjectOrRemoteAddress.FromToken(resultsAddress, resultsType.FullName ?? resultsType.Name, hashCode);
    }

    var invocResults = new InvocationResults
    {
      VoidReturnType = method.ReturnType == typeof(void),
      ReturnedObjectOrAddress = returnValue
    };

    return WrapSuccess(invocResults);
  }
}
