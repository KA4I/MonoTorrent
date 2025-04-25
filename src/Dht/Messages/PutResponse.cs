//
// PutResponse.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//   Roo (implementing BEP44)
//
// Copyright (C) 2008 Alan McGovern
// Copyright (C) 2025 Roo
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using MonoTorrent.BEncoding;
using System.Net; // Added for IPEndPoint
 
namespace MonoTorrent.Dht.Messages
{
    /// <summary>
    /// Represents a response to a 'put' request in the DHT protocol (BEP44).
    /// This response is very simple and primarily acknowledges the request.
    /// </summary>
    internal class PutResponse : ResponseMessage
    {
        /// <summary>
        /// The external endpoint of the sender, if known via NAT traversal.
        /// This isn't directly encoded but might be used by the handler creating this response.
        /// </summary>
        public IPEndPoint? ExternalEndPoint { get; }
 
        public PutResponse (NodeId id, BEncodedValue transactionId, IPEndPoint? externalEndPoint = null)
            : base (id, transactionId)
        {
            ExternalEndPoint = externalEndPoint; // Store it
            // According to BEP44, the response only needs the standard 'r' dictionary with the sender's 'id'.
            // The base class constructor handles this.
        }

        public PutResponse (BEncodedDictionary d)
            : base (d)
        {
        }
    }
}