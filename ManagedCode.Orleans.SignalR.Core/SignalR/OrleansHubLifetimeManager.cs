using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Streams;

namespace ManagedCode.Orleans.SignalR.Core.SignalR;

public class OrleansHubLifetimeManager<THub> : HubLifetimeManager<THub> where THub : Hub
{
    private readonly IClusterClient _clusterClient;
    private readonly HubConnectionStore _connections = new();
    private readonly IOptions<HubOptions>? _globalHubOptions;
    private readonly IOptions<HubOptions<THub>>? _hubOptions;
    private readonly IHubProtocolResolver _hubProtocolResolver;

    private readonly ILogger _logger;
    private readonly IOptions<OrleansSignalROptions> _options;

    public OrleansHubLifetimeManager(ILogger<OrleansHubLifetimeManager<THub>> logger,
        IOptions<OrleansSignalROptions> options,
        IClusterClient clusterClient,
        IHubProtocolResolver hubProtocolResolver,
        IOptions<HubOptions>? globalHubOptions,
        IOptions<HubOptions<THub>>? hubOptions)
    {
        _logger = logger;
        _options = options;
        _clusterClient = clusterClient;
        _hubProtocolResolver = hubProtocolResolver;
        _globalHubOptions = globalHubOptions;
        _hubOptions = hubOptions;
    }
    
    
    public override async Task OnConnectedAsync(HubConnectionContext connection)
    {
        _connections.Add(connection);
        var tasks = new List<Task>();
        tasks.Add(SubscribeToConnection(connection));
        tasks.Add(NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
            .AddConnection(connection.ConnectionId));

        if (!string.IsNullOrEmpty(connection.UserIdentifier))
            tasks.Add(NameHelperGenerator.GetSignalRUserGrain<THub>(_clusterClient, connection.UserIdentifier!)
                .AddConnection(connection.ConnectionId));

        await Task.WhenAll(tasks);
    }

    public override Task OnDisconnectedAsync(HubConnectionContext connection)
    {
        _connections.Remove(connection);

        var invocationChannel = connection.Features.Get<IAsyncStream<InvocationMessage>>();
        var invocationHandler = connection.Features.Get<StreamSubscriptionHandle<InvocationMessage>>();

        // If the bus is null then the Redis connection failed to be established and none of the other connection setup ran
        if (invocationChannel is null && invocationHandler is null)
            return Task.CompletedTask;

        var tasks = new List<Task>();
        tasks.Add(invocationHandler!.UnsubscribeAsync());

        if (!string.IsNullOrEmpty(connection.UserIdentifier))
            tasks.Add(NameHelperGenerator
                .GetSignalRUserGrain<THub>(_clusterClient, connection.UserIdentifier!)
                .RemoveConnection(connection.ConnectionId));

        tasks.Add(NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
            .RemoveConnection(connection.ConnectionId));
        tasks.Add(NameHelperGenerator.GetGroupHolderGrain<THub>(_clusterClient)
            .RemoveConnection(connection.ConnectionId));

        return Task.WhenAll(tasks);
    }

    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient).SendToAll(message);
    }

    public override Task SendAllExceptAsync(string methodName, object?[] args,
        IReadOnlyList<string> excludedConnectionIds,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
            .SendToAllExcept(message, excludedConnectionIds.ToArray());
    }

    public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
            .SendToConnection(message, connectionId);
    }

    public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
            .SendToConnections(message, connectionIds.ToArray());
    }

    public override Task SendGroupAsync(string groupName, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName)
            .SendToGroup(message);
    }

    public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        var tasks = new List<Task>(groupNames.Count);

        foreach (var groupName in groupNames)
            tasks.Add(NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName)
                .SendToGroup(message));

        return Task.WhenAll(tasks);
    }

    public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args,
        IReadOnlyList<string> excludedConnectionIds,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName)
            .SendToGroupExcept(message, excludedConnectionIds.ToArray());
    }

    public override Task SendUserAsync(string userId, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return NameHelperGenerator.GetSignalRUserGrain<THub>(_clusterClient, userId)
            .SendToUser(message);
    }

    public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        var tasks = new List<Task>(userIds.Count);
        foreach (var userId in userIds)
            tasks.Add(NameHelperGenerator.GetSignalRUserGrain<THub>(_clusterClient, userId)
                .SendToUser(message));

        return Task.WhenAll(tasks);
    }

    public override Task AddToGroupAsync(string connectionId, string groupName,
        CancellationToken cancellationToken = new())
    {
        return Task.WhenAll(
            NameHelperGenerator.GetGroupHolderGrain<THub>(_clusterClient).AddConnectionToGroup(connectionId, groupName),
            NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName)
                .AddConnection(connectionId));
    }

    public override Task RemoveFromGroupAsync(string connectionId, string groupName,
        CancellationToken cancellationToken = new())
    {
        return Task.WhenAll(
            NameHelperGenerator.GetGroupHolderGrain<THub>(_clusterClient)
                .RemoveConnectionFromGroup(connectionId, groupName),
            NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName)
                .RemoveConnection(connectionId));
    }

    public override async Task<T> InvokeConnectionAsync<T>(string connectionId, string methodName, object?[] args,
        CancellationToken cancellationToken)
    {
        // send thing
        if (string.IsNullOrEmpty(connectionId))
            throw new ArgumentNullException(nameof(connectionId));

        var connection = _connections[connectionId];

        // ID needs to be unique for each invocation and across servers, we generate a GUID every time, that should provide enough uniqueness guarantees.
        var invocationId = GenerateInvocationId();

        var tcs = new TaskCompletionSource<T>();

        var stream = NameHelperGenerator.GetStream<THub, CompletionMessage>(_clusterClient, _options.Value.StreamProvider,
            invocationId);
        var observer = new SignalRAsyncObserver<CompletionMessage>
        {
            OnNextAsync = completionMessage =>
            {
                if (completionMessage.HasResult)
                    tcs.SetResult((T)completionMessage.Result);
                else
                    tcs.SetException(new Exception(completionMessage.Error));

                return Task.CompletedTask;
            }
        };
        var handler = await stream.SubscribeAsync(observer);
        
        await NameHelperGenerator.GetInvocationGrain<THub>(_clusterClient, invocationId)
            .AddInvocation(new InvocationInfo(connectionId, invocationId, typeof(T)));

        var invocationMessage = new InvocationMessage(invocationId, methodName, args);

        var ssssss = stream.StreamId.ToString();
        
        if (connection == null)
        {
            // TODO: Need to handle other server going away while waiting for connection result
            var invocation = await NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
                .SendToConnection(invocationMessage, connectionId);

            if (invocation == false)
                throw new IOException($"Connection '{connectionId}' does not exist.");
        }
        else
        {
            // We're sending to a single connection
            // Write message directly to connection without caching it in memory
            await connection.WriteAsync(invocationMessage, cancellationToken);
        }


        try
        {
            var result = await tcs.Task;
            _ = NameHelperGenerator.GetInvocationGrain<THub>(_clusterClient, invocationId).RemoveInvocation();
            return result;
        }
        catch
        {
            // ConnectionAborted will trigger a generic "Canceled" exception from the task, let's convert it into a more specific message.
            if (connection?.ConnectionAborted.IsCancellationRequested == true)
                throw new IOException($"Connection '{connectionId}' disconnected.");
            throw;
        }
        finally
        {
            observer.Dispose();
            _ = handler.UnsubscribeAsync();
        }
    }

    public override async Task SetConnectionResultAsync(string connectionId, CompletionMessage result)
    {
        await NameHelperGenerator.GetInvocationGrain<THub>(_clusterClient, result.InvocationId)
            .TryCompleteResult(connectionId, result);
    }

    public override bool TryGetReturnType(string invocationId, [NotNullWhen(true)] out Type? type)
    {
        var result = NameHelperGenerator.GetInvocationGrain<THub>(_clusterClient, invocationId)
            .TryGetReturnType().Result;

        type = result.GetReturnType();
        return result.Result;
    }

    private SignalRAsyncObserver<InvocationMessage> CreateInvocationMessageObserver(HubConnectionContext connection)
    {
        var observer = new SignalRAsyncObserver<InvocationMessage>();

        observer.OnNextAsync = invocation =>
        {
            // This is a Client result we need to setup state for the completion and forward the message to the client
            if (!string.IsNullOrEmpty(invocation.InvocationId))
            {
                var x = 5;

                // await NameHelperGenerator.GetInvocationGrain<THub>(_clusterClient, invocation.InvocationId)
                //     .TryCompleteResult(connection.ConnectionId, completionMessage);
                //
                // CancellationTokenRegistration? tokenRegistration = null;
                // _clientResultsManager.AddInvocation(invocation.InvocationId, (typeof(RawResult), connection.ConnectionId, null, async (_, completionMessage) =>
                //     {
                //         tokenRegistration?.Dispose();
                //         try
                //         {
                //
                //
                //         }
                //         catch (Exception ex)
                //         {
                //             _logger.LogError(completionMessage.InvocationId, ex);
                //         }
                //     }));
                //
                // // TODO: this isn't great
                // tokenRegistration = connection.ConnectionAborted.UnsafeRegister(_ =>
                // {
                //     await NameHelperGenerator.GetInvocationGrain<THub>(_clusterClient, invocation.InvocationId)
                //     var invocationInfo = _clientResultsManager.RemoveInvocation(invocation.InvocationId);
                //     invocationInfo?.Completion(null!, CompletionMessage.WithError(invocation.InvocationId, "Connection disconnected."));
                // }, null);
            }

            // Forward message from other server to client
            // Normal client method invokes and client result invokes use the same message
            return connection.WriteAsync(invocation, CancellationToken.None).AsTask();
        };

        return observer;
    }


    private async Task SubscribeToConnection(HubConnectionContext connection)
    {
        var invocationConnectionChannel = NameHelperGenerator.GetStream<THub, InvocationMessage>(_clusterClient,
            _options.Value.StreamProvider, connection.ConnectionId);
        var invocationHandler =
            await invocationConnectionChannel.SubscribeAsync(CreateInvocationMessageObserver(connection));
        connection.Features.Set(invocationConnectionChannel);
        connection.Features.Set(invocationHandler);
    }
    
    private static string GenerateInvocationId()
    {
        Span<byte> buffer = stackalloc byte[16];
        var success = Guid.NewGuid().TryWriteBytes(buffer);
        Debug.Assert(success);
        // 16 * 4/3 = 21.333 which means base64 encoding will use 22 characters of actual data and 2 characters of padding ('=')
        Span<char> base64 = stackalloc char[24];
        success = Convert.TryToBase64Chars(buffer, base64, out var written);
        Debug.Assert(success);
        Debug.Assert(written == 24);
        // Trim the two '=='
        Debug.Assert(base64.EndsWith("=="));
        return new string(base64[..^2]);
    }
}