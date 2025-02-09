using System.Diagnostics;
using System.Net;
using System.Text;
using CommunityToolkit.HighPerformance;
using Microsoft.Extensions.Logging;
using Rtsp;
using Rtsp.Messages;
using Rtsp.Onvif;
using Rtsp.Rtcp;
using Rtsp.Rtp;
using Rtsp.Sdp;
using RtspClientExample;
using Timer = System.Timers.Timer;

namespace LucHeart.StreamAnalyzer;

public class RtspClient : IDisposable
{
    private class KeepAliveContext;
    private readonly KeepAliveContext _keepAliveContext = new();

    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;

    // Events that applications can receive
    public event EventHandler<NewStreamEventArgs>? NewVideoStream;
    public event EventHandler<NewStreamEventArgs>? NewAudioStream;
    public event EventHandler<SimpleDataEventArgs>? ReceivedVideoData;
    public event EventHandler<SimpleDataEventArgs>? ReceivedAudioData;

    public enum RtpTransport { Udp, Tcp, Multicast };
    public enum MediaRequest { VideoOnly, AudioOnly, VideoAndAudio };
    private enum RtspStatus { WaitingToConnect, Connecting, ConnectFailed, Connected };

    private IRtspTransport? _rtspSocket; // RTSP connection

    private RtspStatus _rtspSocketStatus = RtspStatus.WaitingToConnect;
    // this wraps around a the RTSP tcp_socket stream
    private RtspListener? _rtspClient;

    private RtpTransport _rtpTransport = RtpTransport.Udp; // Mode, either RTP over UDP or RTP over TCP using the RTSP socket
    // Communication for the RTP (video and audio) 
    private IRtpTransport? _videoRtpTransport;
    private IRtpTransport? _audioRtpTransport;

    private Uri? _uri;                      // RTSP URI (username & password will be stripped out
    private string _session = "";             // RTSP Session
    private Authentication? _authentication;
    private NetworkCredential _credentials = new();
    private readonly uint _ssrc = 12345;
    private bool _clientWantsVideo = false; // Client wants to receive Video
    private bool _clientWantsAudio = false; // Client wants to receive Audio

    private Uri? _videoUri = null;            // URI used for the Video Track
    private int _videoPayload = -1;          // Payload Type for the Video. (often 96 which is the first dynamic payload value. Bosch use 35)

    private Uri? _audioUri = null;            // URI used for the Audio Track
    private int _audioPayload = -1;          // Payload Type for the Video. (often 96 which is the first dynamic payload value)
    private string _audioCodec = "";         // Codec used with Payload Types (eg "PCMA" or "AMR")

    /// <summary>
    /// If true, the client must send an "onvif-replay" header on every play request.
    /// </summary>
    private bool _playbackSession = false;

    // Used with RTSP keepalive
    private bool _serverSupportsGetParameter = false;
    private readonly System.Timers.Timer _keepaliveTimer;

    private IPayloadProcessor? _videoPayloadProcessor = null;
    private IPayloadProcessor? _audioPayloadProcessor = null;

    // setup messages still to send
    private readonly Queue<RtspRequestSetup> _setupMessages = new();

    /// <summary>
    /// Called when the Setup command are completed, so we can start the right Play message (with or without playback informations)
    /// </summary>
    public event EventHandler? SetupMessageCompleted;

    // Constructor
    public RtspClient(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<RtspClient>();
        _loggerFactory = loggerFactory;

        _keepaliveTimer = new Timer(TimeSpan.FromSeconds(3));
        _keepaliveTimer.Elapsed += SendKeepAlive;
    }

    public void Connect(Uri url, string? username = null, string? password = null, RtpTransport rtpTransport = RtpTransport.Tcp, MediaRequest mediaRequest = MediaRequest.VideoAndAudio, bool playbackSession = false)
    {
        _uri = url;
        _logger.LogDebug("Connecting to {url} ", url);


        _playbackSession = playbackSession;
            
        try
        {
            if (_uri.UserInfo.Length > 0)
            {
                _credentials = new NetworkCredential(_uri.UserInfo.Split(':')[0], _uri.UserInfo.Split(':')[1]);
                _uri = new Uri(_uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo,
                    UriFormat.UriEscaped));
            }
            else
            {
                _credentials = new NetworkCredential(username, password);
            }
        }
        catch
        {
            _credentials = new NetworkCredential();
        }

        // We can ask the RTSP server for Video, Audio or both. If we don't want audio we don't need to SETUP the audio channal or receive it
        _clientWantsVideo = mediaRequest is MediaRequest.VideoOnly or MediaRequest.VideoAndAudio;
        _clientWantsAudio = mediaRequest is MediaRequest.AudioOnly or MediaRequest.VideoAndAudio;

        // Connect to a RTSP Server. The RTSP session is a TCP connection
        _rtspSocketStatus = RtspStatus.Connecting;
        try
        {
            _rtspSocket = _uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.InvariantCultureIgnoreCase) ?
                new RtspHttpTransport(_uri, _credentials) :
                new RtspTcpTransport(_uri);
        }
        catch
        {
            _rtspSocketStatus = RtspStatus.ConnectFailed;
            _logger.LogWarning("Error - did not connect");
            return;
        }

        if (!_rtspSocket.Connected)
        {
            _rtspSocketStatus = RtspStatus.ConnectFailed;
            _logger.LogWarning("Error - did not connect");
            return;
        }

        _rtspSocketStatus = RtspStatus.Connected;

        // Connect a RTSP Listener to the RTSP Socket (or other Stream) to send RTSP messages and listen for RTSP replies
        _rtspClient = new RtspListener(_rtspSocket, _loggerFactory.CreateLogger<RtspListener>())
        {
            AutoReconnect = true
        };

        _rtspClient.MessageReceived += RtspMessageReceived;
        _rtspClient.Start(); // start listening for messages from the server (messages fire the MessageReceived event)

        // Check the RTP Transport
        // If the RTP transport is TCP then we interleave the RTP packets in the RTSP stream
        // If the RTP transport is UDP, we initialise two UDP sockets (one for video, one for RTCP status messages)
        // If the RTP transport is MULTICAST, we have to wait for the SETUP message to get the Multicast Address from the RTSP server
        this._rtpTransport = rtpTransport;
        if (rtpTransport == RtpTransport.Udp)
        {
            _videoRtpTransport = new UDPSocket(50000, 51000); // give a range of 500 pairs (1000 addresses) to try incase some address are in use
            _audioRtpTransport = new UDPSocket(50000, 51000); // give a range of 500 pairs (1000 addresses) to try incase some address are in use
        }
        if (rtpTransport == RtpTransport.Tcp)
        {
            int nextFreeRtpChannel = 0;
            _videoRtpTransport = new RtpTcpTransport(_rtspClient)
            {
                DataChannel = nextFreeRtpChannel++,
                ControlChannel = nextFreeRtpChannel++,
            };
            _audioRtpTransport = new RtpTcpTransport(_rtspClient)
            {
                DataChannel = nextFreeRtpChannel++,
                ControlChannel = nextFreeRtpChannel++,
            };
        }
        if (rtpTransport == RtpTransport.Multicast)
        {
            // Nothing to do. Will open Multicast UDP sockets after the SETUP command
        }

        SendOptions();
    }
    
    private void TryReconnect()
    {
        if(_rtspSocket is { Connected: true }) return;
        _logger.LogDebug("Reconnecting....");
        _rtspClient?.Reconnect();
        SendOptions();
    }

    private void SendOptions()
    {
        // Send OPTIONS
        // In the Received Message handler we will send DESCRIBE, SETUP and PLAY
        RtspRequest optionsMessage = new RtspRequestOptions
        {
            RtspUri = _uri
        };
        _rtspClient?.SendMessage(optionsMessage);
    }
    
    public void Play()
    {
        if (_rtspSocket is null || _uri is null)
        {
            throw new InvalidOperationException("Not connected");
        }

        // Send PLAY
        var playMessage = new RtspRequestPlay
        {
            RtspUri = _uri,
            Session = _session
        };
        playMessage.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());

        //// Need for old sony camera SNC-CS20
        playMessage.Headers.Add("range", "npt=0.000-");
        if (_playbackSession)
        {
            playMessage.AddRequireOnvifRequest();
            playMessage.AddRateControlOnvifRequest(false);
        }
        _rtspClient?.SendMessage(playMessage);
    }

    public void Pause()
    {
        if (_rtspSocket is null || _uri is null)
        {
            throw new InvalidOperationException("Not connected");
        }

        // Send PAUSE
        RtspRequest pauseMessage = new RtspRequestPause
        {
            RtspUri = _uri,
            Session = _session
        };
        pauseMessage.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
        _rtspClient?.SendMessage(pauseMessage);
    }
    
    public void Stop()
    {
        // Stop the keepalive timer
        _keepaliveTimer.Stop();
        
        // Send TEARDOWN
        RtspRequest teardownMessage = new RtspRequestTeardown
        {
            RtspUri = _uri,
            Session = _session
        };
        teardownMessage.AddAuthorization(_authentication, _uri!, _rtspSocket?.NextCommandIndex() ?? 0);
        _rtspClient?.SendMessage(teardownMessage);

        // clear up any UDP sockets
        _videoRtpTransport?.Stop();
        _audioRtpTransport?.Stop();

        // Drop the RTSP session
        _rtspClient?.Stop();
    }

    // A Video RTP packet has been received.
    private void VideoRtpDataReceived(object? sender, RtspDataEventArgs e)
    {
        if (e.Data.Data.IsEmpty)
            return;

        using var data = e.Data;
        var rtpPacket = new RtpPacket(data.Data.Span);

        if (rtpPacket.PayloadType != _videoPayload)
        {
            // Check the payload type in the RTP packet matches the Payload Type value from the SDP
            _logger.LogDebug("Ignoring this Video RTP payload");
            return; // ignore this data
        }

        if (_videoPayloadProcessor is null)
        {
            _logger.LogWarning("No video Processor");
            return;
        }

        using RawMediaFrame nalUnits = _videoPayloadProcessor.ProcessPacket(rtpPacket); // this will cache the Packets until there is a Frame

        if (nalUnits.Any())
        {
            ReceivedVideoData?.Invoke(this, new(nalUnits.Data, nalUnits.ClockTimestamp));
        }
    }

    // RTP packet (or RTCP packet) has been received.
    private void AudioRtpDataReceived(object? sender, RtspDataEventArgs e)
    {
        if (e.Data.Data.IsEmpty)
            return;

        using var data = e.Data;
        // Received some Audio Data on the correct channel.
        var rtpPacket = new RtpPacket(data.Data.Span);

        // Check the payload type in the RTP packet matches the Payload Type value from the SDP
        if (rtpPacket.PayloadType != _audioPayload)
        {
            _logger.LogDebug("Ignoring this Audio RTP payload");
            return; // ignore this data
        }

        if (_audioPayloadProcessor is null)
        {
            _logger.LogWarning("No parser for RTP payload {audioPayload}", _audioPayload);
            return;
        }

        using var audioFrames = _audioPayloadProcessor.ProcessPacket(rtpPacket);

        if (audioFrames.Any())
        {
            ReceivedAudioData?.Invoke(this, new(audioFrames.Data, audioFrames.ClockTimestamp));
            // AAC
            // Write the audio frames to the file
            //  ReceivedAAC?.Invoke(this, new(audio_codec, audioFrames.Data, aacPayload.ObjectType, aacPayload.FrequencyIndex, aacPayload.ChannelConfiguration, audioFrames.Timestamp));
        }
    }

    // RTCP packet has been received.
    private void RtcpControlDataReceived(object? sender, RtspDataEventArgs e)
    {
        if (e.Data.Data.IsEmpty)
            return;

        if (sender is not IRtpTransport transport)
        {
            _logger.LogWarning("No RTP Transport");
            return;
        }

        _logger.LogDebug("Received a RTCP message ");

        // RTCP Packet
        // - Version, Padding and Receiver Report Count
        // - Packet Type
        // - Length
        // - SSRC
        // - payload

        // There can be multiple RTCP packets transmitted together. Loop ever each one

        var rtcpPacket = new RtcpPacket(e.Data.Data.Span);
        while (!rtcpPacket.IsEmpty)
        {
            if (!rtcpPacket.IsWellFormed)
            {
                _logger.LogWarning("Invalid RTCP packet");
                break;
            }


            // 200 = SR = Sender Report
            // 201 = RR = Receiver Report
            // 202 = SDES = Source Description
            // 203 = Bye = Goodbye
            // 204 = APP = Application Specific Method
            // 207 = XR = Extended Reports

            _logger.LogDebug("RTCP Data. PacketType={rtcp_packet_type}", rtcpPacket.PacketType);

            if (rtcpPacket.PacketType == RtcpPacketUtil.RTCP_PACKET_TYPE_SENDER_REPORT)
            {
                // We have received a Sender Report
                // Use it to convert the RTP timestamp into the UTC time
                var time = rtcpPacket.SenderReport.Clock;
                var rtpTimestamp = rtcpPacket.SenderReport.RtpTimestamp;

                _logger.LogDebug("RTCP time (UTC) for RTP timestamp {timestamp} is {time} SSRC {ssrc}", rtpTimestamp, time, rtcpPacket.SenderSsrc);
                _logger.LogDebug("Packet Count {packetCount} Octet Count {octetCount}", rtcpPacket.SenderReport.PacketCount, rtcpPacket.SenderReport.OctetCount);

                // Send a Receiver Report
                try
                {
                    byte[] rtcpReceiverReport = new byte[8];
                    const int reportCount = 0; // an empty report
                    int length = (rtcpReceiverReport.Length / 4) - 1; // num 32 bit words minus 1
                    RtcpPacketUtil.WriteHeader(
                        rtcpReceiverReport,
                        RtcpPacketUtil.RTCP_VERSION,
                        false,
                        reportCount,
                        RtcpPacketUtil.RTCP_PACKET_TYPE_RECEIVER_REPORT,
                        length,
                        _ssrc);

                    transport.WriteToControlPort(rtcpReceiverReport);
                }
                catch
                {
                    _logger.LogDebug("Error writing RTCP packet");
                }
            }
            rtcpPacket = rtcpPacket.Next;
        }
        e.Data.Dispose();
    }

    // RTSP Messages are OPTIONS, DESCRIBE, SETUP, PLAY etc
    private void RtspMessageReceived(object? sender, RtspChunkEventArgs e)
    {
        if (e.Message is not RtspResponse message)
            return;

        _logger.LogDebug("Received RTSP response to message {originalReques}", message.OriginalRequest);

        // If message has a 401 - Unauthorised Error, then we re-send the message with Authorization
        // using the most recently received 'realm' and 'nonce'
        if (!message.IsOk)
        {
            _logger.LogDebug("Got Error in RTSP Reply {returnCode} {returnMessage}", message.ReturnCode, message.ReturnMessage);

            // The server may send a new nonce after a time, which will cause our keepalives to return a 401
            // error. We do not fail on keepalive and will reauthenticate a failed keepalive.
            // The Axis M5525 Camera has been observed to send a new nonce every 150 seconds.

            if (message.ReturnCode == 401
                && message.OriginalRequest?.Headers.ContainsKey(RtspHeaderNames.Authorization) == true
                && message.OriginalRequest?.ContextData != _keepAliveContext)
            {
                // the authorization failed.
                _logger.LogError("Fail to authenticate stoping here");
                Stop();
                return;
            }

            // Check if the Reply has an Authenticate header.
            if (message.ReturnCode == 401 && message.Headers.TryGetValue(RtspHeaderNames.WWWAuthenticate, out string? value))
            {
                // Process the WWW-Authenticate header
                // EG:   Basic realm="AProxy"
                // EG:   Digest realm="AXIS_WS_ACCC8E3A0A8F", nonce="000057c3Y810622bff50b36005eb5efeae118626a161bf", stale=FALSE
                // EG:   Digest realm="IP Camera(21388)", nonce="534407f373af1bdff561b7b4da295354", stale="FALSE"

                string wwwAuthenticate = value ?? string.Empty;
                _authentication = Authentication.Create(_credentials, wwwAuthenticate);
                _logger.LogDebug("WWW Authorize parsed for {authentication}", _authentication);
            }

            if (message.OriginalRequest?.Clone() is RtspRequest resendMessage)
            {
                resendMessage.AddAuthorization(_authentication, _uri!, _rtspSocket!.NextCommandIndex());
                _rtspClient?.SendMessage(resendMessage);
            }
            return;
        }

        // If we get a reply to OPTIONS then start the Keepalive Timer and send DESCRIBE
        if (message.OriginalRequest is RtspRequestOptions && message.OriginalRequest.ContextData != _keepAliveContext)
        {
            // Check the capabilities returned by OPTIONS
            // The Public: header contains the list of commands the RTSP server supports
            // Eg   DESCRIBE, SETUP, TEARDOWN, PLAY, PAUSE, OPTIONS, ANNOUNCE, RECORD, GET_PARAMETER]}
            var supportedCommand = RTSPHeaderUtils.ParsePublicHeader(message);
            _serverSupportsGetParameter = supportedCommand.Contains("GET_PARAMETER", StringComparer.OrdinalIgnoreCase);
            // Start a Timer to send an Keepalive RTSP command every 20 seconds
            _keepaliveTimer.Enabled = true;

            // Send DESCRIBE
            RtspRequest describeMessage = new RtspRequestDescribe
            {
                RtspUri = _uri,
                Headers = { { "Accept", "application/sdp" } },
            };
            describeMessage.AddAuthorization(_authentication, _uri!, _rtspSocket!.NextCommandIndex());
            _rtspClient?.SendMessage(describeMessage);
        }

        // If we get a reply to DESCRIBE (which was our second command), then prosess SDP and send the SETUP
        if (message.OriginalRequest is RtspRequestDescribe)
        {
            HandleDescribeResponse(message);
        }

        // If we get a reply to SETUP (which was our third command), then we
        // (i) check if the Interleaved Channel numbers have been modified by the camera (eg Panasonic cameras)
        // (ii) check if we have any more SETUP commands to send out (eg if we are doing SETUP for Video and Audio)
        // (iii) send a PLAY command if all the SETUP command have been sent
        if (message.OriginalRequest is RtspRequestSetup)
        {
            _logger.LogDebug("Got reply from Setup. Session is {session}", message.Session);

            // Session value used with Play, Pause, Teardown and and additional Setups
            _session = message.Session ?? "";
            if (_keepaliveTimer != null && message.Timeout > 0 && message.Timeout > _keepaliveTimer.Interval / 1000)
            {
                _keepaliveTimer.Interval = message.Timeout * 1000 / 2;
            }

            bool isVideoChannel = message.OriginalRequest.RtspUri == _videoUri;
            bool isAudioChannel = message.OriginalRequest.RtspUri == _audioUri;
            Debug.Assert(isVideoChannel || isAudioChannel, "Unknown channel response");

            // Check the Transport header
            var transportString = message.Headers[RtspHeaderNames.Transport];
            if (transportString is not null)
            {
                RtspTransport transport = RtspTransport.Parse(transportString);

                // Check if Transport header includes Multicast
                if (transport.IsMulticast)
                {
                    string? multicastAddress = transport.Destination;
                    var videoDataChannel = transport.Port?.First;
                    var videoRtcpChannel = transport.Port?.Second;

                    if (!string.IsNullOrEmpty(multicastAddress)
                        && videoDataChannel.HasValue
                        && videoRtcpChannel.HasValue)
                    {
                        // Create the Pair of UDP Sockets in Multicast mode
                        if (isVideoChannel)
                        {
                            _videoRtpTransport = new MulticastUDPSocket(multicastAddress, videoDataChannel.Value, multicastAddress, videoRtcpChannel.Value);
                        }
                        else if (isAudioChannel)
                        {
                            _audioRtpTransport = new MulticastUDPSocket(multicastAddress, videoDataChannel.Value, multicastAddress, videoRtcpChannel.Value);
                        }
                    }
                }

                // check if the requested Interleaved channels have been modified by the camera
                // in the SETUP Reply (Panasonic have a camera that does this)
                if (transport.LowerTransport == RtspTransport.LowerTransportType.TCP)
                {
                    RtpTcpTransport? tcpTransport = null;
                    if (isVideoChannel)
                    {
                        tcpTransport = _videoRtpTransport as RtpTcpTransport;
                    }

                    if (isAudioChannel)
                    {
                        tcpTransport = _audioRtpTransport as RtpTcpTransport;
                    }
                    if (tcpTransport is not null)
                    {
                        tcpTransport.DataChannel = transport.Interleaved?.First ?? tcpTransport.DataChannel;
                        tcpTransport.ControlChannel = transport.Interleaved?.Second ?? tcpTransport.ControlChannel;
                    }
                } else if(!transport.IsMulticast)
                {
                    UDPSocket? udpSocket = null;
                    if (isVideoChannel)
                    {
                        udpSocket = _videoRtpTransport as UDPSocket;
                    }

                    if (isAudioChannel)
                    {
                        udpSocket = _audioRtpTransport as UDPSocket;
                    }
                    if (udpSocket is not null)
                    {
                        udpSocket.SetDataDestination(_uri!.Host, transport.ServerPort?.First ?? 0);
                        udpSocket.SetControlDestination(_uri!.Host, transport.ServerPort?.Second ?? 0);
                    }
                }

                if (isVideoChannel && _videoRtpTransport is not null)
                {
                    _videoRtpTransport.DataReceived += VideoRtpDataReceived;
                    _videoRtpTransport.ControlReceived += RtcpControlDataReceived;
                    _videoRtpTransport.Start();
                }

                if (isAudioChannel && _audioRtpTransport is not null)
                {
                    _audioRtpTransport.DataReceived += AudioRtpDataReceived;
                    _audioRtpTransport.ControlReceived += RtcpControlDataReceived;
                    _audioRtpTransport.Start();
                }
            }

            // Check if we have another SETUP command to send, then remote it from the list
            if (_setupMessages.Count > 0)
            {
                // send the next SETUP message, after adding in the 'session'
                RtspRequestSetup nextSetup = _setupMessages.Dequeue();
                nextSetup.Session = _session;
                _rtspClient?.SendMessage(nextSetup);
            }
            else
            {
                // use the event for setup completed, so the main program can call the Play command with or without the playback request.
                SetupMessageCompleted?.Invoke(this, EventArgs.Empty);

                //// Send PLAY
                //RtspRequest play_message = new RtspRequestPlay
                //{
                //    RtspUri = _uri,
                //    Session = session
                //};
                //
                //// Need for old sony camera SNC-CS20
                //play_message.Headers.Add("range", "npt=0.000-");
                //
                //play_message.AddAuthorization(_authentication, _uri!, rtspSocket!.NextCommandIndex());
                //rtspClient?.SendMessage(play_message);
            }
        }

        // If we get a reply to PLAY (which was our fourth command), then we should have video being received
        if (message.OriginalRequest is RtspRequestPlay)
        {
            _logger.LogDebug("Got reply from Play {command} ", message.Command);
        }
    }

    private void HandleDescribeResponse(RtspResponse message)
    {
        if (message.Data.IsEmpty)
        {
            _logger.LogWarning("Invalid SDP");
            return;
        }

        // Examine the SDP
        _logger.LogDebug("Sdp:\n{sdp}", Encoding.UTF8.GetString(message.Data.Span));

        SdpFile sdpData;
        using (StreamReader sdpStream = new(message.Data.AsStream()))
        {
            sdpData = SdpFile.ReadLoose(sdpStream);
        }

        // For old sony cameras, we need to use the control uri from the sdp
        var customControlUri = sdpData.Attributs.FirstOrDefault(x => x.Key == "control");
        if (customControlUri is not null && !string.Equals(customControlUri.Value, "*"))
        {
            _uri = new Uri(_uri!, customControlUri.Value);
        }

        // Process each 'Media' Attribute in the SDP (each sub-stream)
        // to look for first supported video substream
        if (_clientWantsVideo)
        {
            foreach (Media media in sdpData.Medias.Where(m => m.MediaType == Media.MediaTypes.video))
            {
                // search the attributes for control, rtpmap and fmtp
                // holds SPS and PPS in base64 (h264 video)
                AttributFmtp? fmtp = media.Attributs.FirstOrDefault(x => x.Key == "fmtp") as AttributFmtp;
                AttributRtpMap? rtpmap = media.Attributs.FirstOrDefault(x => x.Key == "rtpmap") as AttributRtpMap;
                _videoUri = GetControlUri(media);

                int fmtpPayloadNumber = -1;
                if (fmtp != null)
                {
                    fmtpPayloadNumber = fmtp.PayloadNumber;
                }

                // extract h265 donl if available...
                bool h265HasDonl = false;

                if ((rtpmap?.EncodingName?.ToUpper().Equals("H265") ?? false) && !string.IsNullOrEmpty(fmtp?.FormatParameter))
                {
                    var param = H265Parameters.Parse(fmtp.FormatParameter);
                    if (param.ContainsKey("sprop-max-don-diff") && int.TryParse(param["sprop-max-don-diff"], out int donl) && donl > 0)
                    {
                        h265HasDonl = true;
                    }
                }

                // some cameras are really mess with the payload type.
                // must check also the rtpmap for the corrent format to load (sending an h265 payload when giving an h264 stream [Some Bosch camera])

                string payloadName = string.Empty;
                if (rtpmap != null
                    && (((fmtpPayloadNumber > -1 && rtpmap.PayloadNumber == fmtpPayloadNumber) || fmtpPayloadNumber == -1)
                        && rtpmap.EncodingName != null))
                {
                    // found a valid codec
                    payloadName = rtpmap.EncodingName.ToUpper();
                    _videoPayloadProcessor = payloadName switch
                    {
                        "H264" => new H264Payload(null),
                        "H265" => new H265Payload(h265HasDonl, null),
                        "JPEG" => new JPEGPayload(),
                        "MP4V-ES" => new RawPayload(),
                        _ => null,
                    };
                    _videoPayload = media.PayloadType;
                }
                else
                {
                    _videoPayload = media.PayloadType;
                    if (media.PayloadType < 96)
                    {
                        // PayloadType is a static value, so we can use it to determine the codec
                        _videoPayloadProcessor = media.PayloadType switch
                        {
                            26 => new JPEGPayload(),
                            33 => new MP2TransportPayload(),
                            _ => null,
                        };
                        payloadName = media.PayloadType switch
                        {
                            26 => "JPEG",
                            33 => "MP2T",
                            _ => string.Empty,
                        };
                    }
                }

                IStreamConfigurationData? streamConfigurationData = null;

                if (_videoPayloadProcessor is H264Payload && fmtp?.FormatParameter is not null)
                {
                    // If the rtpmap contains H264 then split the fmtp to get the sprop-parameter-sets which hold the SPS and PPS in base64
                    var param = H264Parameters.Parse(fmtp.FormatParameter);
                    var spsPps = param.SpropParameterSets;
                    if (spsPps.Count >= 2)
                    {
                        byte[] sps = spsPps[0];
                        byte[] pps = spsPps[1];
                        streamConfigurationData = new H264StreamConfigurationData() { SPS = sps, PPS = pps };
                    }
                }
                else if (_videoPayloadProcessor is H265Payload && fmtp?.FormatParameter is not null)
                {
                    // If the rtpmap contains H265 then split the fmtp to get the sprop-vps, sprop-sps and sprop-pps
                    // The RFC makes the VPS, SPS and PPS OPTIONAL so they may not be present. In which we pass back NULL values
                    var param = H265Parameters.Parse(fmtp.FormatParameter);
                    var vpsSpsPps = param.SpropParameterSets;
                    if (vpsSpsPps.Count >= 3)
                    {
                        byte[] vps = vpsSpsPps[0];
                        byte[] sps = vpsSpsPps[1];
                        byte[] pps = vpsSpsPps[2];
                        streamConfigurationData = new H265StreamConfigurationData() { VPS = vps, SPS = sps, PPS = pps };
                    }
                }

                // Send the SETUP RTSP command if we have a matching Payload Decoder
                if (_videoPayloadProcessor is not null)
                {
                    RtspTransport? transport = CalculateTransport(_videoRtpTransport);

                    // Generate SETUP messages
                    if (transport != null)
                    {
                        RtspRequestSetup setupMessage = new()
                        {
                            RtspUri = _videoUri
                        };
                        setupMessage.AddTransport(transport);
                        setupMessage.AddAuthorization(_authentication, _uri!, _rtspSocket!.NextCommandIndex());
                        if (_playbackSession) { setupMessage.AddRequireOnvifRequest(); }
                        // Add SETUP message to list of mesages to send
                        _setupMessages.Enqueue(setupMessage);

                        NewVideoStream?.Invoke(this, new(payloadName, streamConfigurationData));
                    }
                    break;
                }
            }
        }

        if (_clientWantsAudio)
        {
            foreach (Media media in sdpData.Medias.Where(m => m.MediaType == Media.MediaTypes.audio))
            {
                // search the attributes for control, rtpmap and fmtp
                AttributFmtp? fmtp = media.Attributs.FirstOrDefault(x => x.Key == "fmtp") as AttributFmtp;
                AttributRtpMap? rtpmap = media.Attributs.FirstOrDefault(x => x.Key == "rtpmap") as AttributRtpMap;

                _audioUri = GetControlUri(media);
                _audioPayload = media.PayloadType;

                IStreamConfigurationData? streamConfigurationData = null;
                if (media.PayloadType < 96)
                {
                    // fixed payload type
                    (_audioPayloadProcessor, _audioCodec) = media.PayloadType switch
                    {
                        0 => (new G711Payload(), "PCMU"),
                        8 => (new G711Payload(), "PCMA"),
                        _ => (null, ""),
                    };
                }
                else
                {
                    // dynamic payload type
                    _audioCodec = rtpmap?.EncodingName?.ToUpper() ?? string.Empty;
                    _audioPayloadProcessor = _audioCodec switch
                    {
                        // Create AAC RTP Parser
                        // Example fmtp is "96 profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=1490"
                        // Example fmtp is ""96 streamtype=5;profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=1210"
                        "MPEG4-GENERIC" when fmtp?["mode"].ToLower() == "aac-hbr" => new AACPayload(fmtp["config"]),
                        "PCMA" => new G711Payload(),
                        "PCMU" => new G711Payload(),
                        "AMR" => new AMRPayload(),
                        _ => null,
                    };
                    if (_audioPayloadProcessor is AACPayload aacPayloadProcessor)
                    {
                        _audioCodec = "AAC";
                        streamConfigurationData = new AacStreamConfigurationData()
                        {
                            ObjectType = aacPayloadProcessor.ObjectType,
                            FrequencyIndex = aacPayloadProcessor.FrequencyIndex,
                            SamplingFrequency = aacPayloadProcessor.SamplingFrequency,
                            ChannelConfiguration = aacPayloadProcessor.ChannelConfiguration
                        };
                    }
                }

                // Send the SETUP RTSP command if we have a matching Payload Decoder
                if (_audioPayloadProcessor is not null)
                {
                    RtspTransport? transport = CalculateTransport(_audioRtpTransport);

                    // Generate SETUP messages
                    if (transport != null)
                    {
                        RtspRequestSetup setupMessage = new()
                        {
                            RtspUri = _audioUri,
                        };
                        setupMessage.AddTransport(transport);
                        setupMessage.AddAuthorization(_authentication, _uri!, _rtspSocket!.NextCommandIndex());
                        if (_playbackSession)
                        {
                            setupMessage.AddRequireOnvifRequest();
                            setupMessage.AddRateControlOnvifRequest(false);
                        }
                        // Add SETUP message to list of mesages to send
                        _setupMessages.Enqueue(setupMessage);
                        NewAudioStream?.Invoke(this, new(_audioCodec, streamConfigurationData));
                    }
                    break;
                }
            }
        }

        if (_setupMessages.Count == 0)
        {
            // No SETUP messages were generated
            // So we cannot continue
            throw new ApplicationException("Unable to setup media stream");
        }

        // Send the FIRST SETUP message and remove it from the list of Setup Messages
        _rtspClient?.SendMessage(_setupMessages.Dequeue());
    }

    private Uri? GetControlUri(Media media)
    {
        Uri? controlUri = null;
        var attrib = media.Attributs.FirstOrDefault(a => a.Key == "control");
        if (attrib is not null)
        {
            string sdpControl = attrib.Value;

            if (sdpControl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                || sdpControl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                // the "track" or "stream id"
                string control = sdpControl; //absolute path
                controlUri = new Uri(control);
            }
            else
            {
                // add trailing / if necessary
                var baseUriWithTrailingSlash = _uri!.ToString().EndsWith('/') ? _uri : new Uri($"{_uri}/");
                // relative path
                controlUri = new Uri(baseUriWithTrailingSlash, sdpControl);
            }
        }
        return controlUri;
    }

    private RtspTransport? CalculateTransport(IRtpTransport? transport)
    {
        return _rtpTransport switch
        {
            // Server interleaves the RTP packets over the RTSP connection
            // Example for TCP mode (RTP over RTSP)   Transport: RTP/AVP/TCP;interleaved=0-1
            RtpTransport.Tcp => new RtspTransport()
            {
                LowerTransport = RtspTransport.LowerTransportType.TCP,
                // Eg Channel 0 for RTP video data. Channel 1 for RTCP status reports
                Interleaved = (transport as RtpTcpTransport)?.Channels ?? throw new ApplicationException("TCP transport asked and no tcp channel allocated"),
            },
            RtpTransport.Udp => new RtspTransport()
            {
                LowerTransport = RtspTransport.LowerTransportType.UDP,
                IsMulticast = false,
                ClientPort = (transport as UDPSocket)?.Ports ?? throw new ApplicationException("UDP transport asked and no udp port allocated"),
            },
            // Server sends the RTP packets to a Pair of UDP ports (one for data, one for rtcp control messages)
            // using Multicast Address and Ports that are in the reply to the SETUP message
            // Example for MULTICAST mode     Transport: RTP/AVP;multicast
            RtpTransport.Multicast => new RtspTransport()
            {
                LowerTransport = RtspTransport.LowerTransportType.UDP,
                IsMulticast = true,
                ClientPort = new PortCouple(5000, 5001)
            },
            _ => null,
        };
    }
    
    private void CheckReconnect(object? sender, System.Timers.ElapsedEventArgs e)
    {
        TryReconnect();
    }

    private void SendKeepAlive(object? sender, System.Timers.ElapsedEventArgs e)
    {
        // Send Keepalive message
        // The ONVIF Standard uses SET_PARAMETER as "an optional method to keep an RTSP session alive"
        // RFC 2326 (RTSP Standard) says "GET_PARAMETER with no entity body may be used to test client or server liveness("ping")"

        // This code uses GET_PARAMETER (unless OPTIONS report it is not supported, and then it sends OPTIONS as a keepalive)
        RtspRequest keepAliveMessage =
            _serverSupportsGetParameter
                ? new RtspRequestGetParameter
                {
                    RtspUri = _uri,
                    Session = _session
                }
                : new RtspRequestOptions
                {
                    // RtspUri = new Uri(url)
                };
        
        keepAliveMessage.ContextData = _keepAliveContext;
        keepAliveMessage.AddAuthorization(_authentication, _uri!, _rtspSocket!.NextCommandIndex());
        _rtspClient?.SendMessage(keepAliveMessage);
    }

    public void Dispose()
    {
        try
        {
            Stop();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Error while stopping the RTSP client during dispose");
        }

        _rtspClient?.Dispose();
        _videoRtpTransport?.Dispose();
        _audioRtpTransport?.Dispose();
        _keepaliveTimer.Dispose();
    }
}