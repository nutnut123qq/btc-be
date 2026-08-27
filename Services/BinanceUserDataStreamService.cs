using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backend.Services;

public class BinanceUserDataStreamService : BackgroundService, IBinanceUserDataStreamService
{
    private readonly HttpClient _http;
    private readonly BinanceTestnetOptions _options;
    private readonly ILogger<BinanceUserDataStreamService> _logger;
    private readonly Microsoft.Extensions.DependencyInjection.IServiceScopeFactory? _scopeFactory;

    private ClientWebSocket? _webSocket;
    private string? _currentListenKey;
    private bool _isConnected;
    private DateTimeOffset? _lastPingTime;
    private DateTimeOffset? _connectedSince;
    private DateTimeOffset? _lastEventReceivedTime;
    private string? _lastEventType;
    private int _reconnectCount;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsConnected => _isConnected;
    public string? CurrentListenKey => _currentListenKey;
    public DateTimeOffset? LastPingTime => _lastPingTime;
    public DateTimeOffset? ConnectedSince => _connectedSince;
    public int ReconnectCount => _reconnectCount;

    public event Func<OrderTradeUpdateEvent, Task>? OnOrderTradeUpdate;
    public event Func<AccountUpdateEvent, Task>? OnAccountUpdate;
    public event Func<string, Task>? OnListenKeyExpired;
    public event Func<bool, Task>? OnConnectionStatusChanged;

    public BinanceUserDataStreamService(
        HttpClient http,
        IOptions<BinanceTestnetOptions> options,
        ILogger<BinanceUserDataStreamService> logger,
        Microsoft.Extensions.DependencyInjection.IServiceScopeFactory? scopeFactory = null)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _scopeFactory = scopeFactory;

        if (!string.IsNullOrEmpty(_options.BaseUrl))
        {
            _http.BaseAddress = new Uri(_options.BaseUrl);
        }
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public StreamStatusDto GetStatus()
    {
        return new StreamStatusDto
        {
            IsConnected = _isConnected,
            CurrentListenKey = MaskKey(_currentListenKey),
            LastPingTime = _lastPingTime,
            ConnectedSince = _connectedSince,
            ReconnectCount = _reconnectCount,
            TradingMode = _options.TradingMode,
            BaseUrl = _options.BaseUrl,
            WsUrl = _options.WsBaseUrl,
            StreamEnabled = _options.StreamEnabled,
            LastEventReceivedTime = _lastEventReceivedTime,
            LastEventType = _lastEventType
        };
    }

    public async Task<string?> CreateListenKeyAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogInformation("[BinanceUserDataStream] ApiKey is not configured. Service is running in simulated/standby mode.");
            return null;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/fapi/v1/listenKey");
            req.Headers.Add("X-MBX-APIKEY", _options.ApiKey);

            using var resp = await _http.SendAsync(req, cancellationToken);
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("[BinanceUserDataStream] Failed to create listenKey: HTTP {StatusCode} - {Body}", resp.StatusCode, json);
                return null;
            }

            var result = JsonSerializer.Deserialize<ListenKeyResponse>(json);
            if (result != null && !string.IsNullOrWhiteSpace(result.ListenKey))
            {
                _currentListenKey = result.ListenKey;
                _lastPingTime = DateTimeOffset.UtcNow;
                _logger.LogInformation("[BinanceUserDataStream] Khởi tạo listenKey thành công: {ListenKey}", MaskKey(_currentListenKey));
                return _currentListenKey;
            }

            _logger.LogWarning("[BinanceUserDataStream] Response did not contain a valid listenKey: {Json}", json);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[BinanceUserDataStream] Exception while creating listenKey");
            return null;
        }
    }

    public async Task<bool> PingListenKeyAsync(string? listenKey = null, CancellationToken cancellationToken = default)
    {
        var keyToPing = listenKey ?? _currentListenKey;
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(keyToPing))
        {
            return false;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, "/fapi/v1/listenKey");
            req.Headers.Add("X-MBX-APIKEY", _options.ApiKey);

            using var resp = await _http.SendAsync(req, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                _lastPingTime = DateTimeOffset.UtcNow;
                _logger.LogInformation("[BinanceUserDataStream] Keep-alive ping 30 phút thành công cho listenKey {ListenKey} lúc {Time:HH:mm:ss dd/MM/yyyy}",
                    MaskKey(keyToPing), _lastPingTime);
                return true;
            }

            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("[BinanceUserDataStream] Keep-alive ping failed: HTTP {StatusCode} - {Body}", resp.StatusCode, body);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[BinanceUserDataStream] Exception sending Keep-alive ping");
            return false;
        }
    }

    public async Task<bool> CloseListenKeyAsync(string? listenKey = null, CancellationToken cancellationToken = default)
    {
        var keyToClose = listenKey ?? _currentListenKey;
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(keyToClose))
        {
            return false;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, "/fapi/v1/listenKey");
            req.Headers.Add("X-MBX-APIKEY", _options.ApiKey);

            using var resp = await _http.SendAsync(req, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("[BinanceUserDataStream] Giải phóng listenKey thành công: {ListenKey}", MaskKey(keyToClose));
                _currentListenKey = null;
                return true;
            }

            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("[BinanceUserDataStream] Failed to delete listenKey: HTTP {StatusCode} - {Body}", resp.StatusCode, body);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[BinanceUserDataStream] Exception deleting listenKey");
            return false;
        }
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("[BinanceUserDataStream] Triggering manual reconnect...");
            if (_webSocket != null)
            {
                try
                {
                    _webSocket.Abort();
                    _webSocket.Dispose();
                }
                catch { }
                _webSocket = null;
            }
            SetConnectionStatus(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[BinanceUserDataStream] Service starting. StreamEnabled={Enabled}, BaseUrl={BaseUrl}, WsUrl={WsUrl}",
            _options.StreamEnabled, _options.BaseUrl, _options.WsBaseUrl);

        if (!_options.StreamEnabled)
        {
            _logger.LogInformation("[BinanceUserDataStream] Stream is disabled via configuration.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogInformation("[BinanceUserDataStream] Không có ApiKey Binance. Service chạy ở chế độ Standby/Simulated.");
            return;
        }

        // Start periodic Keep-alive ping loop in the background
        _ = RunKeepAliveLoopAsync(stoppingToken);

        int retryAttempt = 0;
        int maxBackoff = Math.Max(5, _options.MaxReconnectBackoffSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_currentListenKey))
                {
                    _logger.LogInformation("[BinanceUserDataStream] Đang lấy listenKey mới từ Binance Futures Testnet...");
                    var newKey = await CreateListenKeyAsync(stoppingToken);
                    if (string.IsNullOrWhiteSpace(newKey))
                    {
                        retryAttempt++;
                        int delaySec = (int)Math.Min(Math.Pow(2, Math.Min(retryAttempt, 6)), maxBackoff);
                        _logger.LogWarning("[BinanceUserDataStream] Chưa lấy được listenKey. Thử lại sau {Delay}s (lần {Attempt})...", delaySec, retryAttempt);
                        await Task.Delay(TimeSpan.FromSeconds(delaySec), stoppingToken);
                        continue;
                    }
                }

                // Connect WebSocket to Binance Futures Testnet User Stream
                var wsEndpoint = $"{_options.WsBaseUrl.TrimEnd('/')}/{_currentListenKey}";
                _logger.LogInformation("[BinanceUserDataStream] Đang kết nối WebSocket đến: {WsUrl}", wsEndpoint);

                using var ws = new ClientWebSocket();
                _webSocket = ws;

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                await ws.ConnectAsync(new Uri(wsEndpoint), cts.Token);

                SetConnectionStatus(true);
                retryAttempt = 0;
                _logger.LogInformation("[BinanceUserDataStream] Kết nối WebSocket thành công đến Binance Futures Testnet User Stream!");

                // Message processing loop
                await ProcessWebSocketMessagesAsync(ws, cts.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetConnectionStatus(false);
                retryAttempt++;
                _reconnectCount++;

                int delaySec = (int)Math.Min(Math.Pow(2, Math.Min(retryAttempt, 6)), maxBackoff);
                _logger.LogWarning(ex, "[BinanceUserDataStream] Mất kết nối WebSocket. Auto-reconnect sau {Delay}s (Lần thử {Count})...", delaySec, _reconnectCount);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                SetConnectionStatus(false);
            }
        }

        // Service shutdown
        await CleanupAsync();
    }

    private async Task ProcessWebSocketMessagesAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var messageBuffer = new MemoryStream();

        while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            messageBuffer.SetLength(0);
            WebSocketReceiveResult result;

            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogWarning("[BinanceUserDataStream] WebSocket nhận tín hiệu đóng từ server: {Status} - {Desc}",
                        result.CloseStatus, result.CloseStatusDescription);
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
                    return;
                }

                messageBuffer.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var jsonString = Encoding.UTF8.GetString(messageBuffer.ToArray());
            await HandleStreamMessageAsync(jsonString);
        }
    }

    public async Task HandleStreamMessageAsync(string jsonString)
    {
        if (string.IsNullOrWhiteSpace(jsonString)) return;

        try
        {
            using var doc = JsonDocument.Parse(jsonString);
            var root = doc.RootElement;

            if (!root.TryGetProperty("e", out var eventTypeProp))
            {
                _logger.LogDebug("[BinanceUserDataStream] Nhận gói tin không có trường 'e': {Json}", jsonString);
                return;
            }

            var eventType = eventTypeProp.GetString() ?? string.Empty;
            _lastEventType = eventType;
            _lastEventReceivedTime = DateTimeOffset.UtcNow;

            switch (eventType)
            {
                case "listenKeyExpired":
                    _logger.LogWarning("[BinanceUserDataStream] Nhận sự kiện listenKeyExpired: {Json}", jsonString);
                    var expiredEvent = JsonSerializer.Deserialize<ListenKeyExpiredEvent>(jsonString);
                    _currentListenKey = null; // Mark key as expired so reconnection creates a new one
                    if (OnListenKeyExpired != null && expiredEvent != null)
                    {
                        await SafeInvokeAsync(OnListenKeyExpired, expiredEvent.ListenKey);
                    }
                    if (_webSocket != null && _webSocket.State == WebSocketState.Open)
                    {
                        try
                        {
                            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "listenKeyExpired", CancellationToken.None);
                        }
                        catch { }
                    }
                    break;

                case "ORDER_TRADE_UPDATE":
                    var orderEvent = JsonSerializer.Deserialize<OrderTradeUpdateEvent>(jsonString);
                    if (orderEvent != null)
                    {
                        var o = orderEvent.Order;
                        _logger.LogInformation(
                            "[BinanceUserDataStream] ORDER_TRADE_UPDATE: Symbol={Symbol}, Side={Side}, Type={Type}, Status={Status}, ExecType={ExecType}, Qty={Qty}, Price={Price}, FilledQty={Filled}, RealizedProfit={Profit}",
                            o.Symbol, o.Side, o.OrderType, o.OrderStatus, o.ExecutionType, o.OriginalQuantity, o.OriginalPrice, o.AccumulatedFilledQuantity, o.RealizedProfit);

                        if (_scopeFactory != null)
                        {
                            try
                            {
                                using var scope = _scopeFactory.CreateScope();
                                var handler = scope.ServiceProvider.GetService<IUserDataStreamHandlerService>();
                                if (handler != null)
                                {
                                    await handler.HandleOrderTradeUpdateAsync(orderEvent);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[BinanceUserDataStream] Lỗi khi chuyển tiếp ORDER_TRADE_UPDATE tới UserDataStreamHandlerService");
                            }
                        }

                        if (OnOrderTradeUpdate != null)
                        {
                            await SafeInvokeAsync(OnOrderTradeUpdate, orderEvent);
                        }
                    }
                    break;

                case "ACCOUNT_UPDATE":
                    var accountEvent = JsonSerializer.Deserialize<AccountUpdateEvent>(jsonString);
                    if (accountEvent != null)
                    {
                        var a = accountEvent.AccountInfo;
                        _logger.LogInformation(
                            "[BinanceUserDataStream] ACCOUNT_UPDATE: Reason={Reason}, BalancesCount={Balances}, PositionsCount={Positions}",
                            a.EventReasonType, a.Balances.Count, a.Positions.Count);

                        if (_scopeFactory != null)
                        {
                            try
                            {
                                using var scope = _scopeFactory.CreateScope();
                                var handler = scope.ServiceProvider.GetService<IUserDataStreamHandlerService>();
                                if (handler != null)
                                {
                                    await handler.HandleAccountUpdateAsync(accountEvent);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[BinanceUserDataStream] Lỗi khi chuyển tiếp ACCOUNT_UPDATE tới UserDataStreamHandlerService");
                            }
                        }

                        if (OnAccountUpdate != null)
                        {
                            await SafeInvokeAsync(OnAccountUpdate, accountEvent);
                        }
                    }
                    break;

                default:
                    _logger.LogInformation("[BinanceUserDataStream] Unhandled Event '{EventType}': {Json}", eventType, jsonString);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BinanceUserDataStream] Lỗi parse gói tin WebSocket: {Json}", jsonString);
        }
    }

    private async Task RunKeepAliveLoopAsync(CancellationToken stoppingToken)
    {
        var pingInterval = TimeSpan.FromMinutes(Math.Max(1, _options.PingIntervalMinutes));
        _logger.LogInformation("[BinanceUserDataStream] Vòng lặp Keep-alive bắt đầu với chu kỳ {Minutes} phút", _options.PingIntervalMinutes);

        using var timer = new PeriodicTimer(pingInterval);

        try
        {
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!string.IsNullOrWhiteSpace(_currentListenKey) && _isConnected)
                {
                    _logger.LogInformation("[BinanceUserDataStream] Gửi Keep-alive ping định kỳ...");
                    var ok = await PingListenKeyAsync(_currentListenKey, stoppingToken);
                    if (!ok)
                    {
                        _logger.LogWarning("[BinanceUserDataStream] Ping Keep-alive thất bại. Sẽ làm mới listenKey khi cần.");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BinanceUserDataStream] Lỗi trong vòng lặp Keep-alive");
        }
    }

    private void SetConnectionStatus(bool connected)
    {
        if (_isConnected != connected)
        {
            _isConnected = connected;
            _connectedSince = connected ? DateTimeOffset.UtcNow : null;
            _logger.LogInformation("[BinanceUserDataStream] Trạng thái kết nối thay đổi: IsConnected={Connected}", connected);

            if (OnConnectionStatusChanged != null)
            {
                _ = SafeInvokeAsync(OnConnectionStatusChanged, connected);
            }
        }
    }

    private async Task CleanupAsync()
    {
        _logger.LogInformation("[BinanceUserDataStream] Dọn dẹp tài nguyên và giải phóng listenKey...");
        SetConnectionStatus(false);

        if (_webSocket != null)
        {
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Service stopped", CancellationToken.None);
                }
                _webSocket.Dispose();
            }
            catch { }
            _webSocket = null;
        }

        if (!string.IsNullOrWhiteSpace(_currentListenKey))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await CloseListenKeyAsync(_currentListenKey, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BinanceUserDataStream] Không thể giải phóng listenKey khi shutdown");
            }
        }
    }

    private async Task SafeInvokeAsync<T>(Func<T, Task> handler, T arg)
    {
        try
        {
            await handler.Invoke(arg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BinanceUserDataStream] Lỗi khi xử lý event handler callback");
        }
    }

    private static string? MaskKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (key.Length <= 10) return "***";
        return $"{key[..6]}...{key[^4..]}";
    }
}
