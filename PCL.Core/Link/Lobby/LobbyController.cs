using PCL.Core.App.Localization;
using PCL.Core.Link.EasyTier;
using PCL.Core.Link.Scaffolding;
using PCL.Core.Link.Scaffolding.Client.Models;
using PCL.Core.Link.Scaffolding.Client.Requests;
using PCL.Core.Logging;
using PCL.Core.Utils.Exts;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.IO.Net;
using static PCL.Core.Link.Lobby.LobbyInfoProvider;
using LobbyType = PCL.Core.Link.Scaffolding.Client.Models.LobbyType;
using PCL.Core.Link.McPing;
using PCL.Core.IO.Net.SocketForward;

namespace PCL.Core.Link.Lobby;

/// <summary>
/// The controller of lobby that used for creating Scaffolding entity.
/// </summary>
public sealed class LobbyController
{
    /// <summary>
    /// Demonstrate the current lobby is host or joiner.
    /// </summary>
    public bool IsHost = false;

    /// <summary>
    /// Scaffolding client entity.
    /// </summary>
    public ScaffoldingClientEntity? ScfClientEntity;

    /// <summary>
    /// Scaffolding server entity.
    /// </summary>
    public ScaffoldingServerEntity? ScfServerEntity;

    /// <summary>
    /// Launch a Scaffolding Client.
    /// </summary>
    /// <param name="username">Join user name.</param>
    /// <param name="code">Lobby share code.</param>
    /// <returns>Created <see cref="ScaffoldingClientEntity"/>.</returns>
    public async Task<ScaffoldingClientEntity?> LaunchClientAsync(string username, string code, CancellationToken ct = default)
    {
        try
        {
            var scfEntity = await ScaffoldingFactory
                .CreateClientAsync(username, code, LobbyType.Scaffolding, ct).ConfigureAwait(false);

            ScfClientEntity = scfEntity;

            await scfEntity.Client.ConnectAsync().ConfigureAwait(false);

            var port = await scfEntity.Client.SendRequestAsync(new GetServerPortRequest()).ConfigureAwait(false);

            var hostname = string.Empty;

            while (scfEntity.Client.PlayerList is null)
            {
                await Task.Delay(800).ConfigureAwait(false);
            }

            foreach (var profile in scfEntity.Client.PlayerList)
            {
                if (profile.Kind == PlayerKind.HOST)
                {
                    hostname = profile.Name;
                    LogWrapper.Debug($"大厅创建者的用户名: {hostname}");
                }
            }

            var localPort = await scfEntity.EasyTier
                .AddPortForwardAsync(scfEntity.HostInfo.Ip, port)
                .ConfigureAwait(false);
            var desc = hostname.IsNullOrWhiteSpace()
                ? string.Empty
                : Lang.Text("Link.Lobby.MotdDesc", hostname);

            var tcpPortForForward = NetworkHelper.NewTcpPort();
            McForward = new TcpForwardBuilder()
                .BindLocal((ushort)tcpPortForForward)
                .SetRemote(IPAddress.Loopback, (ushort)localPort)
                .Build();
            McForward.Start();
            McBroadcast = new BroadcastLocal(Lang.Text("Link.Lobby.MotdFormat", desc), tcpPortForForward);
            McBroadcast.Start();

            return scfEntity;
        }
        catch (ArgumentNullException e)
        {
            LogWrapper.Error(e, "大厅创建者的用户名为空");
        }
        catch (ArgumentException e)
        {
            if (e.Message.Contains("lobby code"))
            {
                LogWrapper.Error(e, "大厅编号无效");
            }
            else if (e.Message.Contains("hostname"))
            {
                LogWrapper.Error(e, "大厅创建者的用户名无效");
            }
            else
            {
                LogWrapper.Error(e, "在加入大厅时出现意外的无效参数");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            LogWrapper.Error(e, "在加入大厅时发生意外错误");
        }

        return null;
    }

    /// <summary>
    /// Launch a Scaffolding Server.
    /// </summary>
    /// <param name="username">Host user name.</param>
    /// <param name="port">Minecraft port.</param>
    /// <returns>Created <see cref="ScaffoldingServerEntity"/>.</returns>
    /// <remarks>
    /// Because of event handling of Scaffolding Server. You SHOULD start the server on your own.
    /// </remarks>
    public async Task<ScaffoldingServerEntity?> LaunchServerAsync(string username, int port)
    {
        try
        {
            var scfEntity = ScaffoldingFactory.CreateServer(port, username);
            ScfServerEntity = scfEntity;
            IsHost = true;

            LogWrapper.Info("LobbyController", "Successfully to launch Scaffolding Server.");

            return scfEntity;
        }
        catch (Exception e)
        {
            LogWrapper.Error(e, "Occurred error when launching Scafolding Server.");
        }

        return null;
    }

    /// <summary>
    /// 检查主机的 MC 实例是否可用。
    /// </summary>
    public static async Task<bool> IsHostInstanceAvailableAsync(int port)
    {
        using var ping = McPingServiceFactory.CreateService("127.0.0.1", port);
        var info = await ping.PingAsync().ConfigureAwait(false);

        if (info is not null) return true;

        LogWrapper.Warn("Link", $"本地 MC 局域网实例 ({port}) 疑似已关闭");

        return false;
    }

    /// <summary>
    /// 退出大厅。这将同时关闭 EasyTier 和 MC 端口转发，需要自行清理 UI。
    /// </summary>
    public async Task<int> CloseAsync()
    {
        McForward?.Stop();
        McBroadcast?.Stop();
        try
        {
            if (ScfClientEntity is not null)
            {
                await ScfClientEntity.EasyTier.StopAsync().ConfigureAwait(false);
                await ScfClientEntity.Client.DisposeAsync().ConfigureAwait(false);
            }
            else if (ScfServerEntity is not null)
            {
                await ScfServerEntity.EasyTier.StopAsync().ConfigureAwait(false);
                await ScfServerEntity.Server.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            ScfClientEntity = null;
            ScfServerEntity = null;
        }
        return 0;
    }
}
