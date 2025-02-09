using Rtsp;
using Rtsp.Messages;
using System;

namespace RtspClientExample
{
    public static class RTSPMessageAuthExtension
    {
        public static void AddAuthorization(this RtspRequest message, Authentication? authentication, Uri uri, uint commandCounter)
        {
            if (authentication is null)
            {
                return;
            }

            string authorization = authentication.GetResponse(commandCounter, uri.AbsoluteUri, message.RequestTyped.ToString(), []);
            // remove if already one...
            message.Headers.Remove(RtspHeaderNames.Authorization);
            message.Headers.Add(RtspHeaderNames.Authorization, authorization);
        }
    }
}
