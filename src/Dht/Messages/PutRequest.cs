//
// PutRequest.cs
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


using System;
using System.Collections.Generic; // Added for List<>


using MonoTorrent.BEncoding;

namespace MonoTorrent.Dht.Messages
{
    /// <summary>
    /// Represents a 'put' request in the DHT protocol (BEP44).
    /// Used for both immutable and mutable items.
    /// </summary>
    internal class PutRequest : QueryMessage
    {
        internal static readonly BEncodedString TokenKey = new BEncodedString ("token"); // Made internal
        internal static readonly BEncodedString ValueKey = new BEncodedString ("v"); // Made internal
        internal static readonly BEncodedString PublicKeyKey = new BEncodedString ("k"); // Made internal
        internal static readonly BEncodedString SaltKey = new BEncodedString ("salt"); // Made internal
        internal static readonly BEncodedString SeqKey = new BEncodedString ("seq"); // Made internal
        internal static readonly BEncodedString SignatureKey = new BEncodedString ("sig"); // Made internal
        internal static readonly BEncodedString CasKey = new BEncodedString ("cas"); // Made internal
        static readonly BEncodedString QueryName = new BEncodedString ("put");

        public BEncodedString Token => (BEncodedString) Parameters[TokenKey];
        public BEncodedValue Value => Parameters[ValueKey];

        // Mutable item specific fields
        public BEncodedString? PublicKey {
            get => Parameters.TryGetValue (PublicKeyKey, out BEncodedValue? val) ? (BEncodedString) val : null;
            private set {
                if (value == null)
                    Parameters.Remove (PublicKeyKey);
                else
                    Parameters[PublicKeyKey] = value;
            }
        }

        public BEncodedString? Salt {
            get => Parameters.TryGetValue (SaltKey, out BEncodedValue? val) ? (BEncodedString) val : null;
            private set {
                if (value == null)
                    Parameters.Remove (SaltKey);
                else
                    Parameters[SaltKey] = value;
            }
        }

        public long? SequenceNumber {
            get => Parameters.TryGetValue (SeqKey, out BEncodedValue? val) ? ((BEncodedNumber) val).Number : null;
            private set {
                if (value.HasValue)
                    Parameters[SeqKey] = new BEncodedNumber (value.Value);
                else
                    Parameters.Remove (SeqKey);
            }
        }

        public BEncodedString? Signature {
            get => Parameters.TryGetValue (SignatureKey, out BEncodedValue? val) ? (BEncodedString) val : null;
            private set {
                if (value == null)
                    Parameters.Remove (SignatureKey);
                else
                    Parameters[SignatureKey] = value;
            }
        }

        public long? Cas {
            get => Parameters.TryGetValue (CasKey, out BEncodedValue? val) ? ((BEncodedNumber) val).Number : null;
            private set {
                if (value.HasValue)
                    Parameters[CasKey] = new BEncodedNumber (value.Value);
                else
                    Parameters.Remove (CasKey);
            }
        }

        // Constructor for Immutable items
        public PutRequest (NodeId id, BEncodedString token, BEncodedValue value)
             : base (id, QueryName)
        {
            Parameters.Add (TokenKey, token);
            Parameters.Add (ValueKey, value);
        }

        // Constructor for Mutable items
        public PutRequest (NodeId id, BEncodedString token, BEncodedValue value, BEncodedString publicKey, BEncodedString? salt, long sequenceNumber, BEncodedString signature, long? cas = null)
            : base (id, QueryName)
        {
            Parameters.Add (TokenKey, token);
            Parameters.Add (ValueKey, value);
            PublicKey = publicKey; // Use property setter
            Salt = salt;           // Use property setter
            SequenceNumber = sequenceNumber; // Use property setter
            Signature = signature; // Use property setter
            Cas = cas;             // Use property setter
        }

        public PutRequest (BEncodedDictionary d)
            : base (d)
        {
        }

        public override ResponseMessage CreateResponse (BEncodedDictionary parameters)
        {
            // We need to create PutResponse.cs next
            return new PutResponse (parameters);
        }

        public override void Handle(DhtEngine engine, Node node)
        {
            base.Handle(engine, node);

            // Verify the token
            if (!engine.TokenManager.VerifyToken(node, Token))
            {
                Console.WriteLine($"[PutRequest.Handle] Invalid token received from {node.EndPoint}. Discarding PutRequest.");
                // Send ErrorMessage for bad token (ErrorCode 203)
                var error = new ErrorMessage(TransactionId!, ErrorCode.ProtocolError, "Invalid token provided.");
                engine.MessageLoop.EnqueueSend(error, node, node.EndPoint);
                return;
            }

            // Determine Target ID
            NodeId targetId;
            bool isMutable = PublicKey != null;
            if (isMutable)
            {
                targetId = DhtEngine.CalculateMutableTargetId(PublicKey!, Salt); // PublicKey is checked by isMutable
                Console.WriteLine($"[PutRequest.Handle] Received mutable PutRequest for {targetId} from {node.EndPoint}. Seq: {SequenceNumber}");

                // TODO: Verify signature (requires Ed25519 library) before storing.
                //       If signature is invalid, send an ErrorMessage (e.g., ProtocolError 203).

                // Create StoredDhtItem for mutable data
                // Ensure SequenceNumber is not null for mutable items (should be validated by constructor or earlier)
                if (SequenceNumber.HasValue && Signature != null) // Signature also checked
                {
                    var itemToStore = new StoredDhtItem(Value, PublicKey!, Salt, SequenceNumber.Value, Signature /*, Cas */);
                    // Store the item using the engine's storage mechanism
                    engine.StoreItem(targetId, itemToStore);
                } else {
                     Console.WriteLine($"[PutRequest.Handle] Error: Missing SequenceNumber or Signature for mutable PutRequest for {targetId}.");
                     // Optionally send an error response back
                     var error = new ErrorMessage(TransactionId!, ErrorCode.ProtocolError, "Missing sequence number or signature for mutable put.");
                     engine.MessageLoop.EnqueueSend(error, node, node.EndPoint);
                     return; // Do not proceed to send PutResponse
                }
            }
            else
            {
                // Immutable item - calculate target from value hash
                using (var sha1 = System.Security.Cryptography.SHA1.Create())
                    targetId = new NodeId(sha1.ComputeHash(Value.Encode()));
                Console.WriteLine($"[PutRequest.Handle] Received immutable PutRequest for {targetId} from {node.EndPoint}.");
                // Create and store StoredDhtItem for immutable data
                var itemToStore = new StoredDhtItem(Value);
                engine.StoreItem(targetId, itemToStore);
            }

            // Send PutResponse
            var response = new PutResponse(engine.RoutingTable.LocalNodeId, TransactionId!);
            engine.MessageLoop.EnqueueSend(response, node, node.EndPoint);
        }
    }
}
