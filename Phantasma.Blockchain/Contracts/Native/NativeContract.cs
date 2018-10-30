﻿using System.Text;
using System.Reflection;
using System.Collections.Generic;

using Phantasma.VM.Contracts;
using Phantasma.VM;
using Phantasma.Cryptography;
using Phantasma.Numerics;
using Phantasma.IO;
using System;
using Phantasma.Blockchain.Tokens;

namespace Phantasma.Blockchain.Contracts.Native
{
    public abstract class NativeContract : SmartContract
    {
        private Address _address;

        public override byte[] Script => null;

        private ContractInterface _ABI;
        public override ContractInterface ABI => _ABI;

        private Dictionary<string, MethodInfo> _methodTable = new Dictionary<string, MethodInfo>();

        public NativeContract() : base()
        {
            var type = this.GetType();

            var bytes = Encoding.ASCII.GetBytes(type.Name);
            var hash = CryptoExtensions.Sha256(bytes);
            _address = new Address(hash);

            var srcMethods = type.GetMethods(BindingFlags.Public | BindingFlags.InvokeMethod | BindingFlags.Instance);
            var methods = new List<ContractMethod>();

            var ignore = new HashSet<string>(new string[] { "ToString", "GetType", "Equals", "GetHashCode", "CallMethod", "SetTransaction" });

            foreach (var srcMethod in srcMethods)
            {
                var parameters = new List<VM.VMType>();
                var srcParams = srcMethod.GetParameters();

                var methodName = srcMethod.Name;
                if (methodName.StartsWith("get_"))
                {
                    methodName = methodName.Substring(4);
                }

                if (ignore.Contains(methodName))
                {
                    continue;
                }

                var isVoid = srcMethod.ReturnType == typeof(void);
                var returnType = isVoid ? VMType.None : VMObject.GetVMType(srcMethod.ReturnType);

                bool isValid = isVoid || returnType != VMType.None;
                if (!isValid)
                {
                    continue;
                }

                foreach (var srcParam in srcParams)
                {
                    var paramType = srcParam.ParameterType;
                    var vmtype = VMObject.GetVMType(paramType);

                    if (vmtype != VMType.None)
                    {
                        parameters.Add(vmtype);
                    }
                    else
                    {
                        isValid = false;
                        break;
                    }
                }

                if (isValid)
                {
                    _methodTable[methodName] = srcMethod;
                    var method = new ContractMethod(methodName, returnType, parameters.ToArray());
                    methods.Add(method);
                }
            }

            _ABI = new ContractInterface(methods);
        }

        public object CallMethod(string name, object[] args)
        {
            var method = _methodTable[name];

            var parameters = method.GetParameters();
            for (int i=0; i<parameters.Length; i++)
            {
                var p = parameters[i];
                if (p.ParameterType.IsEnum)
                {
                    var receivedType = args[i].GetType();
                    if (!receivedType.IsEnum)
                    {
                        var val = Enum.Parse(p.ParameterType, args[i].ToString());
                        args[i] = val; 
                    }
                }
            }

            return method.Invoke(this, args);
        }

        private HashSet<Hash> knownTransactions = new HashSet<Hash>();
        internal bool IsKnown(Hash hash)
        {
            return knownTransactions.Contains(hash);
        }

        protected void RegisterHashAsKnown(Hash hash)
        {
            knownTransactions.Add(hash);
        }

        #region SIDE CHAINS
        public bool IsChain(Address address)
        {
            return Runtime.Nexus.FindChainByAddress(address) != null;
        }

        public bool IsRootChain(Address address)
        {
            return (IsChain(address) && address == this.Runtime.Chain.Address);
        }

        public bool IsSideChain(Address address)
        {
            return (IsChain(address) && address != this.Runtime.Chain.Address);
        }

        public void SendTokens(Address targetChain, Address from, Address to, string symbol, BigInteger amount)
        {
            Runtime.Expect(IsWitness(from), "invalid witness");

            if (IsRootChain(this.Runtime.Chain.Address))
            {
                Runtime.Expect(IsSideChain(targetChain), "target must be sidechain");
            }
            else
            {
                Runtime.Expect(IsRootChain(targetChain), "target must be rootchain");
            }

            var otherChain = this.Runtime.Nexus.FindChainByAddress(targetChain);
            /*TODO
            var otherConsensus = (ConsensusContract)otherChain.FindContract(ContractKind.Consensus);
            Runtime.Expect(otherConsensus.IsValidReceiver(from));*/

            var token = this.Runtime.Nexus.FindTokenBySymbol(symbol);
            Runtime.Expect(token != null, "invalid token");
            Runtime.Expect(token.Flags.HasFlag(TokenFlags.Fungible), "must be fungible token");

            var balances = this.Runtime.Chain.GetTokenBalances(token);
            Runtime.Expect(token.Burn(balances, from, amount), "burn failed");

            Runtime.Notify(EventKind.TokenSend, from, new TokenEventData() { symbol = symbol, value = amount, chainAddress = targetChain });
        }

        public void SendToken(Address targetChain, Address from, Address to, string symbol, BigInteger tokenID)
        {
            Runtime.Expect(IsWitness(from), "invalid witness");

            if (IsRootChain(this.Runtime.Chain.Address))
            {
                Runtime.Expect(IsSideChain(targetChain), "target must be sidechain");
            }
            else
            {
                Runtime.Expect(IsRootChain(targetChain), "target must be rootchain");
            }

            var otherChain = this.Runtime.Nexus.FindChainByAddress(targetChain);

            var token = this.Runtime.Nexus.FindTokenBySymbol(symbol);
            Runtime.Expect(token != null, "invalid token");
            Runtime.Expect(!token.Flags.HasFlag(TokenFlags.Fungible), "must be non-fungible token");

            var ownerships = this.Runtime.Chain.GetTokenOwnerships(token);
            Runtime.Expect(ownerships.Take(from, tokenID), "take token failed");

            Runtime.Notify(EventKind.TokenSend, from, new TokenEventData() { symbol = symbol, value = tokenID, chainAddress = targetChain });
        }

        public void SettleBlock(Address sourceChain, Hash hash)
        {
            if (IsRootChain(this.Runtime.Chain.Address))
            {
                Runtime.Expect(IsSideChain(sourceChain), "source must be sidechain");
            }
            else
            {
                Runtime.Expect(IsRootChain(sourceChain), "source must be rootchain");
            }


            Runtime.Expect(!IsKnown(hash), "hash already settled");

            var otherChain = this.Runtime.Nexus.FindChainByAddress(sourceChain);

            var block = otherChain.FindBlockByHash(hash);
            Runtime.Expect(block != null, "invalid block");

            int settlements = 0;

            foreach (Transaction tx in block.Transactions)
            {
                string symbol = null;
                BigInteger value = 0;
                Address targetAddress = Address.Null;

                foreach (var evt in tx.Events)
                {
                    if (evt.Kind == EventKind.TokenSend)
                    {
                        var data = Serialization.Unserialize<TokenEventData>(evt.Data);
                        if (data.chainAddress == this.Runtime.Chain.Address)
                        {
                            symbol = data.symbol;
                            value = data.value;
                            targetAddress = evt.Address;
                        }
                    }
                }

                if (symbol != null)
                {
                    settlements++;
                    Runtime.Expect(value > 0, "value must be greater than zero");
                    Runtime.Expect(targetAddress != Address.Null, "target must not be null");

                    var token = this.Runtime.Nexus.FindTokenBySymbol(symbol);
                    Runtime.Expect(token != null, "invalid token");

                    if (token.Flags.HasFlag(TokenFlags.Fungible))
                    {
                        var balances = this.Runtime.Chain.GetTokenBalances(token);
                        Runtime.Expect(token.Mint(balances, targetAddress, value), "mint failed");
                    }
                    else
                    {
                        var ownerships = this.Runtime.Chain.GetTokenOwnerships(token);
                        Runtime.Expect(ownerships.Give(targetAddress, value), "give token failed");
                    }

                    Runtime.Notify(EventKind.TokenReceive, targetAddress, new TokenEventData() { symbol = symbol, value = value, chainAddress = otherChain.Address });
                }
            }

            Runtime.Expect(settlements > 0, "no settlements in the block");
            RegisterHashAsKnown(hash);
        }
        #endregion
    }
}
