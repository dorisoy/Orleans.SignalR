using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Helpers;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Utilities;

namespace ManagedCode.Orleans.SignalR.Server;

[Reentrant]
[GrainType($"ManagedCode.{nameof(SignalRInvocationGrain)}")]
public class SignalRInvocationGrain : Grain, ISignalRInvocationGrain
{
    private readonly ILogger<SignalRInvocationGrain> _logger;
    private readonly ObserverManager<ISignalRObserver> _observerManager;
    private readonly IPersistentState<InvocationInfo> _stateStorage;

    public SignalRInvocationGrain(ILogger<SignalRInvocationGrain> logger,
        IOptions<OrleansSignalROptions> orleansSignalOptions, IOptions<HubOptions> hubOptions,
        [PersistentState(nameof(SignalRInvocationGrain), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<InvocationInfo> stateStorage)
    {
        _logger = logger;
        _stateStorage = stateStorage;

        var timeSpan = TimeIntervalHelper.GetClientTimeoutInterval(orleansSignalOptions, hubOptions);
        _observerManager = new ObserverManager<ISignalRObserver>(TimeIntervalHelper.AddExpirationIntervalBuffer(timeSpan), _logger);
    }

    public async Task TryCompleteResult(string connectionId, HubMessage message)
    {
        await Task.Yield();
        Logs.TryCompleteResult(_logger, nameof(SignalRInvocationGrain),this.GetPrimaryKeyString(), connectionId);
        _logger.LogInformation("Hub: {PrimaryKeyString}; TryCompleteResult: {ConnectionId}", this.GetPrimaryKeyString(),
            connectionId);
        if (_stateStorage.State == null || _stateStorage.State.ConnectionId != connectionId)
            return;

        await _observerManager.Notify(s => s.OnNextAsync(message));
    }

    public async Task<ReturnType> TryGetReturnType()
    {
        await Task.Yield();
        
        Logs.TryGetReturnType(_logger, nameof(SignalRInvocationGrain),this.GetPrimaryKeyString());
        if (_stateStorage.State == null)
            return new ReturnType();

        return new ReturnType
        {
            Result = true,
            Type = _stateStorage.State.Type
        };
    }

    public async Task AddInvocation(ISignalRObserver observer, InvocationInfo invocationInfo)
    {
        await Task.Yield();
        Logs.AddInvocation(_logger, nameof(SignalRInvocationGrain),this.GetPrimaryKeyString(), invocationInfo.InvocationId, invocationInfo.ConnectionId);

        if(invocationInfo?.InvocationId is null || invocationInfo?.ConnectionId is null)
            return;
        
        _observerManager.Subscribe(observer, observer);
        _stateStorage.State = invocationInfo;
    }

    public async Task<InvocationInfo?> RemoveInvocation()
    {
        await Task.Yield();
        Logs.RemoveInvocation(_logger, nameof(SignalRInvocationGrain),this.GetPrimaryKeyString());
        _observerManager.Clear();
        var into = _stateStorage.State;
        await _stateStorage.ClearStateAsync();
        DeactivateOnIdle();
        return into;
    }

    public async Task Ping(ISignalRObserver observer)
    {
        await Task.Yield();
        Logs.Ping(_logger, nameof(SignalRInvocationGrain),this.GetPrimaryKeyString());
        _observerManager.Subscribe(observer, observer);
    }

    public Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        //ignore for this grain
        Logs.AddConnection(_logger, nameof(SignalRInvocationGrain),this.GetPrimaryKeyString(), connectionId);
        return Task.CompletedTask;
    }

    public async Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        await Task.Yield();
        Logs.RemoveConnection(_logger, nameof(SignalRInvocationGrain),this.GetPrimaryKeyString(), connectionId);
        _observerManager.Unsubscribe(observer);
        _observerManager.Clear();
        await _stateStorage.ClearStateAsync();
        DeactivateOnIdle();
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        Logs.OnDeactivateAsync(_logger, nameof(SignalRInvocationGrain),this.GetPrimaryKeyString());
        
        _observerManager.ClearExpired();

        if (string.IsNullOrEmpty(_stateStorage.State.ConnectionId) ||
            string.IsNullOrEmpty(_stateStorage.State.InvocationId))
            await _stateStorage.ClearStateAsync();
        else
            await _stateStorage.WriteStateAsync();
    }
}