﻿using System.Diagnostics;
using System.Text.Json;
using devicesConnector.Configs;
using devicesConnector.FiscalRegistrar.Helpers;
using devicesConnector.FiscalRegistrar.Objects;
using devicesConnector.FiscalRegistrar.Objects.CountrySpecificData.Russia;
using devicesConnector.Helpers;

namespace devicesConnector.FiscalRegistrar.Devices.Russia;

public partial class VikiPrint : IFiscalRegistrarDevice
{
    private readonly Device _deviceConfig;

    public VikiPrint(Device device)
    {
        _deviceConfig = device;
    }


    public KkmStatus GetStatus()
    {
        LogHelper.Write("Запрос статуса на Вики");

        var status = new KkmStatus();

        var los = GetListOfStatuses(out var fatal, out var current, out var document);

        CheckResult(los);

        status.SessionStatus = Enums.SessionStatuses.Close;

        if (current[2] == 1)
        {
            status.SessionStatus = Enums.SessionStatuses.Open;
        }

        if (current[3] == 1)
        {
            status.SessionStatus = Enums.SessionStatuses.OpenMore24Hours;
        }

        status.CheckStatus = Enums.CheckStatuses.Close;

        if (document[0] != 0)
        {
            status.CheckStatus = Enums.CheckStatuses.Open;
        }

        if (GetInfo(1, RequestType.CounterAndRegisters, out var sessionNumber))
        {
            status.SessionNumber = int.TryParse(sessionNumber, out var n) ? n : 1;
        }

        if (GetInfo(2, RequestType.CounterAndRegisters, out var checkNumber))
        {
            status.CheckNumber = int.TryParse(checkNumber, out var n) ? n : 1;
        }

        if (GetInfo(1, RequestType.KkmInfo, out var factoryNumber))
        {
            status.FactoryNumber = factoryNumber;
        }

        if (GetInfo(2, RequestType.KkmInfo, out var softwareVersion))
        {
            status.SoftwareVersion = softwareVersion;
        }

        if (GetInfo(21, RequestType.KkmInfo, out var modelCode))
        {
            status.Model = modelCode;
        }

        if (GetInfo(17, RequestType.KkmInfo, out var sessionStart))
        {
            status.SessionStarted = GetDateTimeFromString(sessionStart);
        }

        if (GetInfo(14, RequestType.KkmInfo, out var fnEnd))
        {
            status.FnDateEnd = GetDateTimeFromString(fnEnd);
        }


        if (GetInfo(7, RequestType.KkmInfo, out var cashSum))
        {
            cashSum = cashSum.Replace(".", string.Empty);

            if (int.TryParse(cashSum, out var kkmSum))
            {
                status.CashSum = kkmSum/100M;
            }
            
        }

        var versionInfo = FileVersionInfo.GetVersionInfo(PiritlibDllPath);
        status.DriverVersion = new Version(versionInfo.FileVersion ?? "1.0.0.0");

        return status;
    }

    public void OpenSession(Cashier cashier)
    {
        //открывается автоматически, метода нет в драйвере
    }

    public void GetReport(Enums.ReportTypes type, Cashier cashier)
    {
        var cashierName = PrepareCashierNameAndInn(cashier);

        var result = type switch
        {
            Enums.ReportTypes.ZReport => lib_zReport(cashierName),
            Enums.ReportTypes.XReport => lib_xReport(cashierName),
            Enums.ReportTypes.XReportWithGoods => throw new NotSupportedException(),
            _ => throw new ArgumentOutOfRangeException(nameof(type), this, null)
        };

        CheckResult(result);
    }

    public void OpenReceipt(ReceiptData? receipt)
    {
        if (receipt == null)
        {
            throw new NullReferenceException();
        }

        var cashierName = receipt.Cashier.Name;

        DocTypes docType;

        if (receipt.FiscalType == Enums.ReceiptFiscalTypes.Fiscal)
        {
            docType = receipt.OperationType switch
            {
                Enums.ReceiptOperationTypes.Sale => DocTypes.SaleCheck,
                Enums.ReceiptOperationTypes.ReturnSale => DocTypes.ReturnCheck,
                Enums.ReceiptOperationTypes.Buy => throw new NotImplementedException(),
                Enums.ReceiptOperationTypes.ReturnBuy => throw new NotImplementedException(),
                _ => throw new ArgumentOutOfRangeException()
            };

            cashierName = PrepareCashierNameAndInn(receipt.Cashier);
        }
        else
        {
            docType = DocTypes.Service;
        }

        // печать чека на бумаге 1 - вкл, 129 - выкл
        //var r = SetPrintCheck(dc.CheckPrinter.FiscalKkm.IsAlwaysNoPrintCheck ? 129 : 1);
        //CheckResult(r);

        var taxIndex = receipt.CountrySpecificData.Deserialize<Objects.CountrySpecificData.Russia.ReceiptData>()
            ?.TaxVariantIndex ?? 0;

        var openDocResult = OpenDocument(docType, 1, cashierName, taxIndex);
        CheckResult(openDocResult);


        if (receipt.FiscalType == Enums.ReceiptFiscalTypes.Fiscal)
        {
            var digitalReceiptAddress = receipt
                .CountrySpecificData
                .Deserialize<Objects.CountrySpecificData.Russia.ReceiptData>()?
                .DigitalReceiptAddress;

            if (digitalReceiptAddress?.IsNullOrEmpty() == false)
            {
                var setClientAddressResult = lib_setClientAddress(digitalReceiptAddress);

                CheckResult(setClientAddressResult);
            }


            //похоже надо оплачивать это
            // SetClientNameInn(checkData.Client);
        }

        var ffdV = _deviceConfig.DeviceSpecificConfig.Deserialize<KkmConfig>()?.FfdVersion;

        if (ffdV == Enums.FFdVersions.Ffd120)
        {
            Ffd120CodeValidation(receipt);
        }



    
    }

    public void CloseReceipt()
    {
        var r = CloseDocument();

        CheckResult(r);
    }

    public void CancelReceipt()
    {
        var r = lib_CancelDocument();

        CheckResult(r);
    }

    public void RegisterItem(ReceiptItem item)
    {
        var ffdV = _deviceConfig
            .DeviceSpecificConfig.Deserialize<KkmConfig>()?
            .FfdVersion;

        switch (ffdV)
        {
            case Enums.FFdVersions.Offline:
            case Enums.FFdVersions.Ffd100:
            {
                RegisterFfd000Ffd100(item);
                return;
            }
            case Enums.FFdVersions.Ffd105:
            case Enums.FFdVersions.Ffd110:
            {
                RegisterFfd105Ffd110(item);
                return;
            }
            case Enums.FFdVersions.Ffd120:
            {
                RegisterFfd120(item);
                return;
            }
            default:
                RegisterFfd000Ffd100(item);
                return;
        }
    }

    public void RegisterPayment(ReceiptPayment payment)
    {
        var r = lib_addPayment((byte) payment.MethodIndex, (int) (payment.Sum * 100), C1251To866(""));
        CheckResult(r);
    }

    public void PrintText(string text)
    {
        //throw new NotImplementedException();
    }

    public void Connect()
    {
        if (File.Exists(PiritlibDllPath) == false)
        {
            throw new FileNotFoundException();
        }


        var comPort = _deviceConfig.Connection.ComPort;

        if (comPort == null)
        {
            throw new NullReferenceException();
        }

        var openPortResult = lib_OpenPort(
            comPort.PortName,
            comPort.Speed);

        CheckResult(openPortResult);

        if (IsNeedInitialization())
        {

            var r = lib_commandStart();


            CheckResult(r);
        }
    }

    public void Disconnect()
    {
        //todo: проверять,что соединение было установлено


        var r = lib_ClosePort();

        CheckResult(r);
    }

    public void CashIn(decimal sum, Cashier cashier)
    {
        CashInOut(sum, cashier);
    }

    public void CashOut(decimal sum, Cashier cashier)
    {
        CashInOut(-sum, cashier);
    }

    public void CutPaper()
    {
        throw new NotImplementedException();
    }

    public void OpenCashBox()
    {
        throw new NotImplementedException();
    }


    public void Dispose()
    {
        Disconnect();
    }

    /// <summary>
    /// Регистрация позиции по ФФД 1.2
    /// </summary>
    /// <param name="item">Позиция в чеке</param>
    private void RegisterFfd120(ReceiptItem item)
    {
        var ruData = item.CountrySpecificData.Deserialize<ReceiptItemData>();

        if (ruData?.FfdData == null)
        {
            throw new NullReferenceException();
        }

      


        if (ruData.MarkingInfo != null)
        {
            var markCode = RuKkmHelper.PrepareMarkCodeForFfd120(ruData.MarkingInfo.RawCode);

            if (markCode.IsNullOrEmpty() == false)
            {
                var addMarkingResult = AddItemMarkingCode(markCode, (int) ruData.MarkingInfo.EstimatedStatus,
                    (int) ruData.FfdData.Unit,
                    (int) ruData.MarkingInfo.ValidationResultKkm);
                CheckResult(addMarkingResult);
            }
        }

       


        var q = ((int) ruData.FfdData.Unit).ToString("N0");

        var addPositionResult = lib_addPositionLarge(C1251To866(item.Name), string.Empty, (double) item.Quantity,
            (double) item.Price, (byte) (item.TaxRateIndex ?? 0), 0,
            (byte) item.DepartmentIndex, 0, 0,
            (int) ruData.FfdData.Method, (int) ruData.FfdData.Subject, q);


        CheckResult(addPositionResult);
    }

    /// <summary>
    /// Регистрация позиции по ФФД 1.05, 1.1
    /// </summary>
    /// <param name="item"></param>
    /// <exception cref="NullReferenceException"></exception>
    private void RegisterFfd105Ffd110(ReceiptItem item)
    {
        //маркировка для ФФД ниже 1,2 не поддерживается

        var ruData = item.CountrySpecificData.Deserialize<ReceiptItemData>();

        if (ruData == null)
        {
            throw new NullReferenceException();
        }

        var res = AddPosition(item.Name, string.Empty, item.Quantity, item.Price, item.TaxRateIndex ?? 0,
            item.DepartmentIndex, (int) ruData.FfdData.Method, (int) ruData.FfdData.Subject);

        CheckResult(res);
    }

    /// <summary>
    /// Регистрация позиции по оффлайн, ФФД 1.0
    /// </summary>
    /// <param name="item"></param>
    private void RegisterFfd000Ffd100(ReceiptItem item)
    {
        var res = lib_addPosition(C1251To866(item.Name), string.Empty, (double) item.Quantity, (double) item.Price,
            (byte) (item.TaxRateIndex ?? 0), 0, (byte) item.DepartmentIndex);


        CheckResult(res);
    }


    private void CheckResult(int resultCode)
    {
        if (resultCode == 0)
        {
            return; //успешно выполнено
        }

        //todo: разбор кодов ошибок
        var errType = resultCode switch
        {
            10 => Enums.ErrorTypes.SessionMore24Hour,
            _ => Enums.ErrorTypes.Unknown
        };

        

        var ex = new KkmException(string.Empty, errType, resultCode, string.Empty);

        throw ex;
    }

    private void CashInOut(decimal sum, Cashier cashier)
    {
        var cashierName = PrepareCashierNameAndInn(cashier);

        var docType = sum > 0 ? DocTypes.CashIncome : DocTypes.CashOutcome;

        var openDocResult = OpenDocument(docType, 1, cashierName);

        CheckResult(openDocResult);

        var cashOutResult = lib_cashInOut("", (long) (sum * 100M));

        CheckResult(cashOutResult);

        var closeDocResult = CloseDocument();

        CheckResult(closeDocResult);
    }
}