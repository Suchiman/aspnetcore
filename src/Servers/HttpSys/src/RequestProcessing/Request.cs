// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using System.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.AspNetCore.HttpSys.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Server.HttpSys;

internal sealed partial class Request
{
    private X509Certificate2? _clientCert;
    // TODO: https://github.com/aspnet/HttpSysServer/issues/231
    // private byte[] _providedTokenBindingId;
    // private byte[] _referredTokenBindingId;

    private BoundaryType _contentBoundaryType;

    private long? _contentLength;
    private RequestStream? _nativeStream;

    private AspNetCore.HttpSys.Internal.SocketAddress? _localEndPoint;
    private AspNetCore.HttpSys.Internal.SocketAddress? _remoteEndPoint;

    private IReadOnlyDictionary<int, ReadOnlyMemory<byte>>? _requestInfo;

    private bool _isDisposed;

    internal Request(RequestContext requestContext)
    {
        // TODO: Verbose log
        RequestContext = requestContext;
        _contentBoundaryType = BoundaryType.None;

        RequestId = requestContext.RequestId;
        // For HTTP/2 Http.Sys assigns each request a unique connection id for use with API calls, but the RawConnectionId represents the real connection.
        UConnectionId = requestContext.ConnectionId;
        RawConnectionId = requestContext.RawConnectionId;
        SslStatus = requestContext.SslStatus;

        KnownMethod = requestContext.VerbId;
        Method = requestContext.GetVerb()!;

        RawUrl = requestContext.GetRawUrl()!;

        var cookedUrl = requestContext.GetCookedUrl();
        QueryString = cookedUrl.GetQueryString() ?? string.Empty;

        var rawUrlInBytes = requestContext.GetRawUrlInBytes();
        var originalPath = RequestUriBuilder.DecodeAndUnescapePath(rawUrlInBytes);

        PathBase = string.Empty;
        Path = originalPath;

        // 'OPTIONS * HTTP/1.1'
        if (KnownMethod == HttpApiTypes.HTTP_VERB.HttpVerbOPTIONS && string.Equals(RawUrl, "*", StringComparison.Ordinal))
        {
            PathBase = string.Empty;
            Path = string.Empty;
        }
        else
        {
            var prefix = requestContext.Server.Options.UrlPrefixes.GetPrefix((int)requestContext.UrlContext);
            // Prefix may be null if the requested has been transfered to our queue
            if (!(prefix is null))
            {
                if (originalPath.Length == prefix.PathWithoutTrailingSlash.Length)
                {
                    // They matched exactly except for the trailing slash.
                    PathBase = originalPath;
                    Path = string.Empty;
                }
                else
                {
                    // url: /base/path, prefix: /base/, base: /base, path: /path
                    // url: /, prefix: /, base: , path: /
                    PathBase = originalPath.Substring(0, prefix.PathWithoutTrailingSlash.Length); // Preserve the user input casing
                    Path = originalPath.Substring(prefix.PathWithoutTrailingSlash.Length);
                }
            }
            else if (requestContext.Server.Options.UrlPrefixes.TryMatchLongestPrefix(IsHttps, cookedUrl.GetHost()!, originalPath, out var pathBase, out var path))
            {
                PathBase = pathBase;
                Path = path;
            }
        }

        ProtocolVersion = RequestContext.GetVersion();

        Headers = new RequestHeaders(RequestContext);

        User = RequestContext.GetUser();

        if (IsHttps)
        {
            GetTlsHandshakeResults();
        }

        // GetTlsTokenBindingInfo(); TODO: https://github.com/aspnet/HttpSysServer/issues/231

        // Finished directly accessing the HTTP_REQUEST structure.
        RequestContext.ReleasePins();
        // TODO: Verbose log parameters
    }

    internal ulong UConnectionId { get; }

    internal ulong RawConnectionId { get; }

    // No ulongs in public APIs...
    public long ConnectionId => (long)RawConnectionId;

    internal ulong RequestId { get; }

    private SslStatus SslStatus { get; }

    private RequestContext RequestContext { get; }

    // With the leading ?, if any
    public string QueryString { get; }

    public long? ContentLength
    {
        get
        {
            if (_contentBoundaryType == BoundaryType.None)
            {
                // Note Http.Sys adds the Transfer-Encoding: chunked header to HTTP/2 requests with bodies for back compat.
                var transferEncoding = Headers[HeaderNames.TransferEncoding].ToString();
                if (string.Equals("chunked", transferEncoding.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    _contentBoundaryType = BoundaryType.Chunked;
                }
                else
                {
                    string? length = Headers[HeaderNames.ContentLength];
                    if (length != null &&
                        long.TryParse(length.Trim(), NumberStyles.None, CultureInfo.InvariantCulture.NumberFormat, out var value))
                    {
                        _contentBoundaryType = BoundaryType.ContentLength;
                        _contentLength = value;
                    }
                    else
                    {
                        _contentBoundaryType = BoundaryType.Invalid;
                    }
                }
            }

            return _contentLength;
        }
    }

    public RequestHeaders Headers { get; }

    internal HttpApiTypes.HTTP_VERB KnownMethod { get; }

    internal bool IsHeadMethod => KnownMethod == HttpApiTypes.HTTP_VERB.HttpVerbHEAD;

    public string Method { get; }

    public Stream Body => EnsureRequestStream() ?? Stream.Null;

    private RequestStream? EnsureRequestStream()
    {
        if (_nativeStream == null && HasEntityBody)
        {
            _nativeStream = new RequestStream(RequestContext);
        }
        return _nativeStream;
    }

    public bool HasRequestBodyStarted => _nativeStream?.HasStarted ?? false;

    public long? MaxRequestBodySize
    {
        get => EnsureRequestStream()?.MaxSize;
        set
        {
            EnsureRequestStream();
            if (_nativeStream != null)
            {
                _nativeStream.MaxSize = value;
            }
        }
    }

    public string PathBase { get; }

    public string Path { get; }

    public bool IsHttps => SslStatus != SslStatus.Insecure;

    public string RawUrl { get; }

    public Version ProtocolVersion { get; }

    public bool HasEntityBody
    {
        get
        {
            // accessing the ContentLength property delay creates _contentBoundaryType
            return (ContentLength.HasValue && ContentLength.Value > 0 && _contentBoundaryType == BoundaryType.ContentLength)
                || _contentBoundaryType == BoundaryType.Chunked;
        }
    }

    private AspNetCore.HttpSys.Internal.SocketAddress RemoteEndPoint
    {
        get
        {
            if (_remoteEndPoint == null)
            {
                _remoteEndPoint = RequestContext.GetRemoteEndPoint()!;
            }

            return _remoteEndPoint;
        }
    }

    private AspNetCore.HttpSys.Internal.SocketAddress LocalEndPoint
    {
        get
        {
            if (_localEndPoint == null)
            {
                _localEndPoint = RequestContext.GetLocalEndPoint()!;
            }

            return _localEndPoint;
        }
    }

    // TODO: Lazy cache?
    public IPAddress? RemoteIpAddress => RemoteEndPoint.GetIPAddress();

    public IPAddress? LocalIpAddress => LocalEndPoint.GetIPAddress();

    public int RemotePort => RemoteEndPoint.GetPort();

    public int LocalPort => LocalEndPoint.GetPort();

    public string Scheme => IsHttps ? Constants.HttpsScheme : Constants.HttpScheme;

    // HTTP.Sys allows you to upgrade anything to opaque unless content-length > 0 or chunked are specified.
    internal bool IsUpgradable => ProtocolVersion < HttpVersion.Version20 && !HasEntityBody && ComNetOS.IsWin8orLater;

    internal WindowsPrincipal User { get; }

    public SslProtocols Protocol { get; private set; }

    public CipherAlgorithmType CipherAlgorithm { get; private set; }

    public int CipherStrength { get; private set; }

    public HashAlgorithmType HashAlgorithm { get; private set; }

    public int HashStrength { get; private set; }

    public ExchangeAlgorithmType KeyExchangeAlgorithm { get; private set; }

    public int KeyExchangeStrength { get; private set; }

    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> RequestInfo
    {
        get
        {
            if (_requestInfo == null)
            {
                _requestInfo = RequestContext.GetRequestInfo();
            }
            return _requestInfo;
        }
    }

    private void GetTlsHandshakeResults()
    {
        var handshake = RequestContext.GetTlsHandshake();

        Protocol = handshake.Protocol;
        // The OS considers client and server TLS as different enum values. SslProtocols choose to combine those for some reason.
        // We need to fill in the client bits so the enum shows the expected protocol.
        // https://docs.microsoft.com/windows/desktop/api/schannel/ns-schannel-_secpkgcontext_connectioninfo
        // Compare to https://referencesource.microsoft.com/#System/net/System/Net/SecureProtocols/_SslState.cs,8905d1bf17729de3
#pragma warning disable CS0618 // Type or member is obsolete
        if ((Protocol & SslProtocols.Ssl2) != 0)
        {
            Protocol |= SslProtocols.Ssl2;
        }
        if ((Protocol & SslProtocols.Ssl3) != 0)
        {
            Protocol |= SslProtocols.Ssl3;
        }
#pragma warning restore CS0618 // Type or Prmember is obsolete
#pragma warning disable SYSLIB0039 // TLS 1.0 and 1.1 are obsolete
        if ((Protocol & SslProtocols.Tls) != 0)
        {
            Protocol |= SslProtocols.Tls;
        }
        if ((Protocol & SslProtocols.Tls11) != 0)
        {
            Protocol |= SslProtocols.Tls11;
        }
#pragma warning restore SYSLIB0039
        if ((Protocol & SslProtocols.Tls12) != 0)
        {
            Protocol |= SslProtocols.Tls12;
        }
        if ((Protocol & SslProtocols.Tls13) != 0)
        {
            Protocol |= SslProtocols.Tls13;
        }

        CipherAlgorithm = handshake.CipherType;
        CipherStrength = (int)handshake.CipherStrength;
        HashAlgorithm = handshake.HashType;
        HashStrength = (int)handshake.HashStrength;
        KeyExchangeAlgorithm = handshake.KeyExchangeType;
        KeyExchangeStrength = (int)handshake.KeyExchangeStrength;
    }

    public X509Certificate2? ClientCertificate
    {
        get
        {
            if (_clientCert == null && SslStatus == SslStatus.ClientCert)
            {
                try
                {
                    _clientCert = RequestContext.GetClientCertificate();
                }
                catch (CryptographicException ce)
                {
                    Log.ErrorInReadingCertificate(RequestContext.Logger, ce);
                }
                catch (SecurityException se)
                {
                    Log.ErrorInReadingCertificate(RequestContext.Logger, se);
                }
            }

            return _clientCert;
        }
    }

    public bool CanDelegate => !(HasRequestBodyStarted || RequestContext.Response.HasStarted);

    // Populates the client certificate.  The result may be null if there is no client cert.
    // TODO: Does it make sense for this to be invoked multiple times (e.g. renegotiate)? Client and server code appear to
    // enable this, but it's unclear what Http.Sys would do.
    public async Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken = default(CancellationToken))
    {
        if (SslStatus == SslStatus.Insecure)
        {
            // Non-SSL
            return null;
        }
        // TODO: Verbose log
        if (_clientCert != null)
        {
            return _clientCert;
        }
        cancellationToken.ThrowIfCancellationRequested();

        var certLoader = new ClientCertLoader(RequestContext, cancellationToken);
        try
        {
            await certLoader.LoadClientCertificateAsync();
            // Populate the environment.
            if (certLoader.ClientCert != null)
            {
                _clientCert = certLoader.ClientCert;
            }
            // TODO: Expose errors and exceptions?
        }
        catch (Exception)
        {
            if (certLoader != null)
            {
                certLoader.Dispose();
            }
            throw;
        }
        return _clientCert;
    }
    /* TODO: https://github.com/aspnet/WebListener/issues/231
    private byte[] GetProvidedTokenBindingId()
    {
        return _providedTokenBindingId;
    }

    private byte[] GetReferredTokenBindingId()
    {
        return _referredTokenBindingId;
    }
    */
    // Only call from the constructor so we can directly access the native request blob.
    // This requires Windows 10 and the following reg key:
    // Set Key: HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\HTTP\Parameters to Value: EnableSslTokenBinding = 1 [DWORD]
    // Then for IE to work you need to set these:
    // Key: HKLM\Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_ENABLE_TOKEN_BINDING
    // Value: "iexplore.exe"=dword:0x00000001
    // Key: HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_ENABLE_TOKEN_BINDING
    // Value: "iexplore.exe"=dword:00000001
    // TODO: https://github.com/aspnet/WebListener/issues/231
    // TODO: https://github.com/aspnet/WebListener/issues/204 Move to NativeRequestContext
    /*
    private unsafe void GetTlsTokenBindingInfo()
    {
        var nativeRequest = (HttpApi.HTTP_REQUEST_V2*)_nativeRequestContext.RequestBlob;
        for (int i = 0; i < nativeRequest->RequestInfoCount; i++)
        {
            var pThisInfo = &nativeRequest->pRequestInfo[i];
            if (pThisInfo->InfoType == HttpApi.HTTP_REQUEST_INFO_TYPE.HttpRequestInfoTypeSslTokenBinding)
            {
                var pTokenBindingInfo = (HttpApi.HTTP_REQUEST_TOKEN_BINDING_INFO*)pThisInfo->pInfo;
                _providedTokenBindingId = TokenBindingUtil.GetProvidedTokenIdFromBindingInfo(pTokenBindingInfo, out _referredTokenBindingId);
            }
        }
    }
    */
    internal uint GetChunks(ref int dataChunkIndex, ref uint dataChunkOffset, byte[] buffer, int offset, int size)
    {
        return RequestContext.GetChunks(ref dataChunkIndex, ref dataChunkOffset, buffer, offset, size);
    }

    // should only be called from RequestContext
    internal void Dispose()
    {
        if (!_isDisposed)
        {
            // TODO: Verbose log
            _isDisposed = true;
            RequestContext.Dispose();
            (User?.Identity as WindowsIdentity)?.Dispose();
            _nativeStream?.Dispose();
        }
    }

    internal void SwitchToOpaqueMode()
    {
        if (_nativeStream == null)
        {
            _nativeStream = new RequestStream(RequestContext);
        }
        _nativeStream.SwitchToOpaqueMode();
    }

    private static partial class Log
    {
        [LoggerMessage(LoggerEventIds.ErrorInReadingCertificate, LogLevel.Debug, "An error occurred reading the client certificate.", EventName = "ErrorInReadingCertificate")]
        public static partial void ErrorInReadingCertificate(ILogger logger, Exception exception);
    }
}
