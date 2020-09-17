﻿using Acrelec.Library.Logger;
using Acrelec.Mockingbird.Payment.Configuration;
using Acrelec.Mockingbird.Payment.Contracts;
using System;
using System.Text;
using System.IO;
using System.Reflection;
using System.ServiceModel;


namespace Acrelec.Mockingbird.Payment
{
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.PerCall)]
    public class PaymentService : IPaymentService
    {
        private static readonly string ticketPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "ticket");

        /// <summary>
        /// Get the configuratiion data
        /// </summary>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public Result Init(RuntimeConfiguration configuration)
        {
            Log.Info("Init method started...");

            Log.Info($"IP Address value : {configuration.IpAddress}");

            try
            {
                if (configuration == null)
                {
                    Log.Info("Can not set configuration to null.");
                    return ResultCode.GenericError;
                }

                if (configuration.PosNumber <= 0)
                {
                    Log.Info($"Invalid PosNumber {configuration.PosNumber}.");
                    return ResultCode.GenericError;
                }
                if (configuration.IpAddress == string.Empty)
                {
                    Log.Info($"Invalid IPAddress {configuration.IpAddress}.");
                    return ResultCode.GenericError;
                }

                using (var api = new ECRUtilATLApi())
                {
                    var connectResult = api.Connect(configuration.IpAddress);
                    
                    Log.Info($"Connect Result: {connectResult}");

                    if (connectResult != ECRUtilATLErrMsg.OK)
                    {
                        return new Result<PaymentData>((ResultCode)connectResult);
                    }

                    RuntimeConfiguration.Instance = configuration;
                    Heartbeat.Instance.Start();
                    Log.Info("Init success!");

                    return ResultCode.Success;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return ResultCode.GenericError;
            }
            finally
            {
                Log.Info("Init method finished.");
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public Result Test()
        {
            var alive = Heartbeat.Instance?.Alive == true;
            Log.Debug($"Test status: {alive}");
            return alive ? ResultCode.Success : ResultCode.GenericError;
        }

        /// <summary>
        /// Payment method
        /// </summary>
        /// <param name="amount"></param>
        /// <returns></returns>
        public Result<PaymentData> Pay(int amount)
        {
            Log.Info("Pay method started...");
            Log.Info($"Amount = {amount/100.0}.");
            Result<PaymentData> transactionResult = null;

            try
            {
                if (File.Exists(ticketPath))
                {
                    File.Delete(ticketPath);
                }

                if (amount <= 0)
                {
                    Log.Info("Invalid pay amount.");
                    return ResultCode.GenericError;
                }

                var config = RuntimeConfiguration.Instance;

                var data = new PaymentData();

                Log.Info("Calling payment driver...");

                using (var api = new ECRUtilATLApi())
                {
                    var connectResult = api.Connect(config.IpAddress);
                    var disconnectResult = ECRUtilATLErrMsg.OK;


                    Log.Info($"Connect Result: {connectResult}");

                    if (connectResult != ECRUtilATLErrMsg.OK)
                    {
                        return new Result<PaymentData>((ResultCode)connectResult);
                    }

                    var payResult = api.Pay(amount, out var payResponse);
                    Log.Info($"Pay Result: {payResult}");
                    Log.Info($"Pay Response Data: {Utils.GetTransactionOutResult(payResponse.TransactionStatus)}");

                    if (payResult != ECRUtilATLErrMsg.OK)
                    {
                        return new Result<PaymentData>((ResultCode)connectResult);
                    }

                    data.Result = (PaymentResult)Utils.GetTransactionOutResult(payResponse.TransactionStatus);

                    if (data.Result != PaymentResult.Successful)
                    {
                        if (data.Result == PaymentResult.Failed)
                        {
                            Log.Info("Payment Failed.");

                            //persist the transaction
                            PersistTransaction(payResponse);

                            //print the payment ticket for an error
                            //
                            CreateCustomerTicket("-----\n\nPayment failure with\nyour card or issuer\nNO payment has been taken.\n\nPlease try again with another card,\nor at a manned till.\n\n-----");
                            data.HasClientReceipt = true;
                        }
                     
                        return new Result<PaymentData>((ResultCode)payResult, data: data);
                    }

                    data.PaidAmount = amount;

                    //check if a swipe card used reverse the transaction and cancel the transaction
                    if (payResponse.EntryMethod == "2")
                    {

                        Log.Info("This transaction requires a signature. We will reverse it");
                        var ReverseResult = api.Reverse(amount, out var ReverseResponse);

                        Log.Info($"Reverse Response Data: { Utils.GetTransactionOutResult(ReverseResponse.TransactionStatus)}");

                        //create customer error receipt
                        //
                        CreateCustomerTicket("-----\n\nPayment failure with\nyour card or issuer\nNO payment has been taken.\n\nPlease try again with another card,\nor at a manned till.\n\n-----");

                        //persist the transaction for records
                        PersistTransaction(ReverseResponse);

                        data.PaidAmount = 0;
                        data.HasClientReceipt = true;

                        //cancel the transaction
                        Log.Info("Cancelling the transaction");

                        return new Result<PaymentData>(ResultCode.TransactionCancelled, data: data);

                    }

                    //create customer receipt if success
                    if ((ECRUtilATLErrMsg)Convert.ToInt32(payResponse.DiagRequestOut) == ECRUtilATLErrMsg.OK)
                    {
                        Log.Info($"transaction status: {Utils.DiagTxnStatus(payResponse.TransactionStatus)}");

                        if (Utils.GetTransactionOutResult(payResponse.TransactionStatus) == TransactionResult.Successful)
                        {
                            Log.Info($"Transaction Successful");
                            transactionResult = new Result<PaymentData>(ResultCode.Success, data: data);
                            Log.Info("Payment succeeded.");

                            CreateCustomerTicket(payResponse);
                            data.HasClientReceipt = true;
                        }
                    }

                    //persist the transaction
                    PersistTransaction(payResponse);

                    disconnectResult = api.Disconnect();
                    Log.Info($"Disconnect Result: {disconnectResult}");
                }


                return transactionResult;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return ResultCode.GenericError;
            }
            finally
            {
                Log.Info("Pay method finished.");
            }
        }

        /// <summary>
        /// Shutdown
        /// </summary>
        public void Shutdown()
        {
            Log.Info("Shutting down...");
            Program.ManualResetEvent.Set();
        }

        /// <summary>
        /// Persist the transaction as Text file
        /// with Customer and Merchant receiept
        /// </summary>
        /// <param name="result"></param>
        private void PersistTransaction(TransactionResponse result)
        {
            try
            {
                var config = AppConfiguration.Instance;
                var outputDirectory = Path.GetFullPath(config.OutPath);
                var outputPath = Path.Combine(outputDirectory, $"{DateTime.Now:yyyyMMddHHmmss}_ticket.txt");

                if (!Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                StringBuilder customerReceipt = new StringBuilder();
                StringBuilder merchantReceipt = new StringBuilder();

                //get the reponse details for the ticket
                customerReceipt.Append($"\nCUSTOMER RECEIPT\n");
                customerReceipt.Append("**********************\n\n");
                customerReceipt.Append($"MERCHANT NAME:  {result.MerchantName}\n");         
                customerReceipt.Append($"MERCHANT ADDR1: {result.MerchantAddress1}\n");   
                customerReceipt.Append($"MERCHANT ADDR2: {result.MerchantAddress2}\n");    
                customerReceipt.Append($"ACQUIRER MERCHANT ID: {result.AcquirerMerchantID}\n");
                customerReceipt.Append($"ENTRY METHOD: {Utils.CardEntryMethod(result.EntryMethod)}\n");
                customerReceipt.Append($"TID: {result.TerminalId}\n");      
                customerReceipt.Append($"AID: {result.AID}\n");                 
                customerReceipt.Append($"CARD SCHEME NAME: {result.CardSchemeName}\n");     
                customerReceipt.Append($"PAN: {result.PAN}\n");              
                customerReceipt.Append($"PAN SEQUNCE NUMBER : PAN.SEQ {result.PANSeqNum}\n");      
                customerReceipt.Append($"TRANSACTION TYPE: {Utils.GetTransactionTypeString(Convert.ToInt32(result.TransactionType))}\n");
                customerReceipt.Append($"Currency:{Utils.GetCurrencySymbol(Convert.ToInt32(result.Currency))}\n");
                customerReceipt.Append($"AMOUNT: {Utils.GetCurrencySymbol(Convert.ToInt32(result.Currency))}{Utils.FormatReceiptAmount(result.TransactionAmount)}\n");  
                customerReceipt.Append($"TOTAL AMOUNT: {Utils.FormatReceiptAmount(result.TotalAmount)}\n");   
                customerReceipt.Append($"CVM: {Utils.CardVerification(result.CVM)}\n");
                customerReceipt.Append($"HOST MESSAGE: {result.HostMessage}\n\n");
                customerReceipt.Append("***********************\n");
                customerReceipt.Append($"{Utils.DiagTxnStatus(result.TransactionStatus)}\n");
                customerReceipt.Append("***********************\n");


                //get the reponse details for the ticket
                merchantReceipt.Append($"\n\nMERCHANT RECEIPT\n");
                merchantReceipt.Append("**********************\n\n");
                merchantReceipt.Append($"Acquirer Merchant ID: {result.AcquirerMerchantID}\n");
                merchantReceipt.Append($"MERCHANT NAME:  {result.MerchantName}\n");
                merchantReceipt.Append($"MERCHANT ADDR1: {result.MerchantAddress1}\n");
                merchantReceipt.Append($"MERCHANT ADDR2: {result.MerchantAddress2}\n");
                merchantReceipt.Append($"ENTRY METHOD: {Utils.CardEntryMethod(result.EntryMethod)}\n");
                merchantReceipt.Append($"TID: {result.TerminalId}\n");
                merchantReceipt.Append($"AID: {result.AID}\n");
                merchantReceipt.Append($"CARD SCHEME NAME: {result.CardSchemeName}\n");
                merchantReceipt.Append($"PAN: {result.PAN}\n");
                merchantReceipt.Append($"PAN SEQUNCE NUMBER : PAN.SEQ {result.PANSeqNum}\n");
                merchantReceipt.Append($"TRANSACTION TYPE: {Utils.GetTransactionTypeString(Convert.ToInt32(result.TransactionType))}\n");
                merchantReceipt.Append($"Currency: {Utils.GetCurrencySymbol(Convert.ToInt32(result.Currency))}\n");
                merchantReceipt.Append($"AMOUNT: {Utils.GetCurrencySymbol(Convert.ToInt32(result.Currency))}{Utils.FormatReceiptAmount(result.TransactionAmount)}\n");
                merchantReceipt.Append($"TOTAL AMOUNT: {Utils.FormatReceiptAmount(result.TotalAmount)}\n");
                merchantReceipt.Append($"Transaction DATE TIME: {result.TxnDateTime}\n");
                merchantReceipt.Append($"CVM: {Utils.CardVerification(result.CVM)}\n");
                merchantReceipt.Append($"HOST MESSAGE: {result.HostMessage}\n");
                merchantReceipt.Append($"ACQUIRER RESPONSE CODE: {result.AcquirerResponseCode}\n");
                merchantReceipt.Append($"RECEIPT NUMBER: {result.ReceiptNumber}\n\n");
                merchantReceipt.Append("***********************\n");
                merchantReceipt.Append($"{Utils.DiagTxnStatus(result.TransactionStatus)}\n");
                merchantReceipt.Append("***********************\n");

                Log.Info("Persisting Customer and Merchant ticket to {0}", outputPath);
                //Write the new ticket
                File.WriteAllText(outputPath, customerReceipt.ToString() + merchantReceipt.ToString());
            }
            catch (Exception ex)
            {
                Log.Error("Persist Transaction exception.");
                Log.Error(ex);
            }
        }


        //overload the customer ticket to check to return a string
        // output on error
        /// <summary>
        ///  Create Customer Ticket to output the reciept error string
        /// </summary>
        /// <param name="ticket"></param>
        private static void CreateCustomerTicket(string ticket)
        {
            try
            {
                Log.Info($"Persisting Customer ticket to {ticketPath}");

                //Write the new ticket
                File.WriteAllText(ticketPath, ticket);

            }
            catch (Exception ex)
            {
                Log.Error("Error persisting ticket.");
                Log.Error(ex);
            }
        }

        /// <summary>
        /// Create Customer Ticket to output the reciept
        /// </summary>
        /// <param name="ticket"></param>
        private static void CreateCustomerTicket(TransactionResponse ticket)
        {
            StringBuilder ticketContent  = new StringBuilder();

            //get the reponse details for the ticket
         
            ticketContent.Append($"\n\tCUSTOMER RECEIPT\n");
            ticketContent.Append("\t*********************\n\n");
            ticketContent.Append($"\t{ticket.MerchantName}\n");         // Merchant name
            ticketContent.Append($"\t{ticket.MerchantAddress1}\n");     // Merchant Addr1
            ticketContent.Append($"\t{ticket.MerchantAddress2}\n");     // Merchant Addr2
            ticketContent.Append($"\t{ticket.AcquirerMerchantID}\t");   // Aquirer Merchant ID name
            ticketContent.Append($"\t{ticket.TerminalId}\n");           // Terminal ID
            ticketContent.Append($"\t{ticket.CardSchemeName}\n");       // Card Schema name
            ticketContent.Append($"\t{ticket.AID}\n");                  // AID
            ticketContent.Append($"\t{ticket.PAN}\n");                  // Pan
            ticketContent.Append($"\tICC PAN.SEQ {ticket.PANSeqNum}\n");            // Pan Seq num
            ticketContent.Append($"\t{Utils.CardEntryMethod(ticket.EntryMethod)}\n");
            ticketContent.Append($"\t{Utils.GetTransactionTypeString(Convert.ToInt32(ticket.TransactionType))}\n");      // Transaction Type
            ticketContent.Append("\tCARD HOLDER COPY\n");
            ticketContent.Append($"\tPURCHASE AMOUNT: {Utils.GetCurrencySymbol(Convert.ToInt32(ticket.Currency))}{Utils.FormatReceiptAmount(ticket.TransactionAmount)}\n");    //Amount
            ticketContent.Append($"\t{DateTime.Now.ToShortTimeString()} {DateTime.Now.ToShortDateString()}\n");
            ticketContent.Append($"\t{Utils.CardVerification(ticket.CVM)}\n");
            ticketContent.Append("\n\tTHANK YOU\n");
            ticketContent.Append($"\t{ticket.HostMessage}\n");          // Host Message
            ticketContent.Append("\n\t***********************\n");
            ticketContent.Append($"\t{Utils.DiagTxnStatus(ticket.TransactionStatus)}\n");
            ticketContent.Append("\t***********************\n");


            try
            {
                Log.Info($"Persisting Customer ticket to {ticketPath}");

                //Write the new ticket
                File.WriteAllText(ticketPath, ticketContent.ToString());

            }
            catch (Exception ex)
            {
                Log.Error("Error persisting ticket.");
                Log.Error(ex);
            }
        }
    }
}
