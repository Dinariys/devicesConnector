﻿using System.Text.Json;
using devicesConnector.Common;
using devicesConnector.FiscalRegistrar.Drivers;
using devicesConnector.FiscalRegistrar.Objects;

namespace devicesConnector.FiscalRegistrar.Devices.Russia;

public class KkmServerDevice : IFiscalRegistrarDevice
{
    private KkmServerDriver _driver;

    private KkmServerDriver.KkmPrintCheck _kkmCheckCommand;

    public KkmServerDevice(DeviceConnection.LanConnection lanConnection)
    {
        _driver = new KkmServerDriver(lanConnection);
    }



    public KkmStatus GetStatus()
    {
        var c = new KkmServerDriver.KkmGetInfo();

     var r=  _driver.SendCommand(c);

     var answer =r.Rezult.Deserialize< KkmServerDriver.KkmServerKktInfoAnswer> ();

        var status = new KkmStatus
        {
            SessionStatus = answer.Info.SessionState switch
            {
                1 => KkmStatus.SessionStatuses.Close,
                2 => KkmStatus.SessionStatuses.Open,
                3 => KkmStatus.SessionStatuses.OpenMore24Hours,
                _ => KkmStatus.SessionStatuses.Unknown
            },
            CheckStatus = KkmStatus.CheckStatuses.Close,
            CheckNumber = answer.CheckNumber,
            SessionNumber = answer.SessionNumber,
            SoftwareVersion = answer.Info.Firmware_Version,
            FnDateEnd = answer.Info.FN_DateEnd,
            CashSum = answer.Info.BalanceCash
            
        };





        return status;
    }

    public void OpenSession(Cashier cashier)
    {
        var c = new KkmServerDriver.KkmOpenSession();
        _driver.SendCommand(c);

    }

    public void GetReport(Enums.ReportTypes type, Cashier cashier)
    {
        switch (type)
        {
            case Enums.ReportTypes.ZReport:
                _driver.SendCommand(new KkmServerDriver.KkmCloseShift
                {
                    CashierName = cashier.Name,
                    CashierVATIN = cashier.TaxId
                });
                break;
            case Enums.ReportTypes.XReport:
                _driver.SendCommand(new KkmServerDriver.KkmGetXReport
                {

                });
                break;
            case Enums.ReportTypes.XReportWithGoods:
                throw new NotSupportedException();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }

      
    }

    /// <summary>
    /// Открываем чек
    /// </summary>
    /// <param name="receipt">Данные чека</param>
    /// <exception cref="NullReferenceException"></exception>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public void OpenReceipt(ReceiptData? receipt)
    {
        if (receipt == null)
        {
            throw new NullReferenceException();
        }

        //регистрация кассира
        _kkmCheckCommand = new KkmServerDriver.KkmPrintCheck()
        {
            CashierName = receipt.Cashier.Name,
            CashierVATIN = receipt.Cashier.TaxId

        };

        //регистрация покупателя
        if (receipt.Contractor != null)
        {
            _kkmCheckCommand.ClientInfo = receipt.Contractor.Name;
            _kkmCheckCommand.ClientINN = receipt.Contractor.TaxId;
        }

     

        //установка типа чека
        _kkmCheckCommand.TypeCheck = receipt.OperationType switch
        {
            Enums.ReceiptOperationTypes.Sale => 0,
            Enums.ReceiptOperationTypes.ReturnSale => 1,
            Enums.ReceiptOperationTypes.Buy => 10,
            Enums.ReceiptOperationTypes.ReturnBuy => 11,
            _ => throw new ArgumentOutOfRangeException()
        };

        //признак фискальности
        _kkmCheckCommand.IsFiscalCheck = receipt.FiscalType == Enums.ReceiptFiscalTypes.Fiscal;

    
        //специфичные для РФ данные
        #region Специфичные для РФ данные

        RuReceiptData? ruData = receipt.CountrySpecificData.Deserialize<RuReceiptData>();

     

        if (ruData != null)
        {
            if (ruData.DigitalReceiptAddress != null)
            {
                _kkmCheckCommand.ClientAddress = ruData.DigitalReceiptAddress;
            }

            _kkmCheckCommand.TaxVariant = ruData.TaxVariantIndex;
        }

        #endregion



      
        

       


    }

    public void CloseReceipt()
    {
        _driver.SendCommand(_kkmCheckCommand);
        
    }

    public void RegisterItem(ReceiptItem item)
    {

        //значение налоговой ставки (НДС)
       var taxValue = item.TaxRateIndex switch
        {
            1 => -1,
            2 => 0,
            3 => 10,
            4 => 20,
            5 => 110,
            6 => 120,
            _ => -1
        };

 
        //позиция в чеке
       var cs = new KkmServerDriver.KkmPrintCheck.CheckString
       {
           //позиция для регистрации товара
           Register = new KkmServerDriver.KkmPrintCheck.Register
           {
               Name = item.Name,
               Price = item.Price,
               Quantity = item.Quantity,
               Department = item.DepartmentIndex ,
               Tax = taxValue
           }
       };

       //специфичные для РФ данные
       RuReceiptItemData? ruData = item.CountrySpecificData.Deserialize<RuReceiptItemData>();


       if (ruData != null)
       {
           cs.Register.SignCalculationObject = (int) ruData.FfdData.Subject;
           cs.Register.SignMethodCalculation = (int) ruData.FfdData.Method;


           //todo: работа с маркировкой
       }

       _kkmCheckCommand.CheckStrings.Add(cs);


    }

    public void RegisterPayment(ReceiptPayment payment)
    {
        switch (payment.MethodIndex)
        {
            case 1:
                _kkmCheckCommand.Cash = payment.Sum;
                break;
            case 2:
                _kkmCheckCommand.ElectronicPayment = payment.Sum;
                break;
            case 103:
                _kkmCheckCommand.AdvancePayment  = payment.Sum;
                break;
            case 104:
                _kkmCheckCommand.Credit = payment.Sum;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(payment.MethodIndex));
        }
    }

    public void PrintText(string text)
    {
    }

  
    public void CashIn(decimal sum, Cashier cashier)
    {
        var c = new KkmServerDriver.KkmCashIn
        {
            CashierName = cashier.Name,
            CashierVATIN = cashier.TaxId,
            Amount = sum
        };

        _driver.SendCommand(c);

    }

    public void CashOut(decimal sum, Cashier cashier)
    {
        var c = new KkmServerDriver.KkmCashOut
        {
            CashierName = cashier.Name,
            CashierVATIN = cashier.TaxId,
            Amount = sum
        };

        _driver.SendCommand(c);
    }
}