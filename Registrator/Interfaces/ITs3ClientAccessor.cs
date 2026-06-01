using System;
using TSLib.Full;

namespace Registrator.Interfaces;

public interface ITs3ClientAccessor
{
    TsFullClient? Client { get; }
    event EventHandler<TsFullClient>? ClientReady;
    event EventHandler? ClientLost;
    void SetClient(TsFullClient client);
    void ClearClient();
}
