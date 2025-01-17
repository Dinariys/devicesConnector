﻿using System.Text.Json;
using System.Text.Json.Nodes;
using devicesConnector.Configs;
using devicesConnector.FiscalRegistrar.Commands;

namespace devicesConnector.Common;

/// <summary>
/// Репозиторий очереди команд
/// </summary>
public class CommandsQueueRepository
{
    /// <summary>
    /// Статичная очередь команд на выполнение
    /// </summary>
    private static readonly Queue<CommandQueue> CommandsQueue = new();

    /// <summary>
    /// Все команды (для получения статусов по запросу)
    /// todo: перенос в БД
    /// </summary>
    public static List<CommandQueue> CommandsHistory = new();

    /// <summary>
    /// Объект очереди
    /// </summary>
    public class CommandQueue
    {
        /// <summary>
        /// ИД команды
        /// </summary>
        public string CommandId { get; set; }

        /// <summary>
        /// Команда
        /// </summary>
        public JsonNode Command { get; set; }

        /// <summary>
        /// Текущий статус
        /// </summary>
        public Answer.Statuses Status { get; set; }

        /// <summary>
        /// Результат выполнения команды
        /// </summary>
        public JsonNode? Result { get; set; }
    }

    /// <summary>
    /// Добавить команду в очередь
    /// </summary>
    /// <param name="command">Команда</param>
    public void AddToQueue(JsonNode command)
    {
        var commandId = command.Deserialize<DeviceCommand>()?.CommandId;

        if (CommandsHistory.Any(x =>
                x.Command.Deserialize<DeviceCommand>()?.CommandId == commandId))
        {
            throw new Exception("Команда с указанным ИД уже есть в очереди");
        }

        var cq = new CommandQueue
        {
            CommandId = commandId ,
            Command = command,
            Status = Answer.Statuses.Wait
        };

       
        //добавляем в очередь и историю
        CommandsHistory.Add(cq);
        CommandsQueue.Enqueue(cq);
    }

    /// <summary>
    /// Получаем результат выполнения команды
    /// </summary>
    /// <param name="commandId">Идентификатор команды</param>
    /// <returns></returns>
    public CommandQueue GetCommandState(string commandId)
    {
        var ch= CommandsHistory.SingleOrDefault(x => x.CommandId == commandId);

        if(ch == null)
        {
            throw new NullReferenceException($"Команда с {commandId} не найдена");
        }

        return ch;
    }

    /// <summary>
    /// Инициализация обработки очереди
    /// </summary>
    public static void Init()
    {
        Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    DoCommand();
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                }
            }
        });
    }

    /// <summary>
    /// Выполнение команды
    /// </summary>
    private static void DoCommand()
    {
        CommandQueue? cq = null;

        try
        {
            if (CommandsQueue.Any() == false)
            {
                return;
            }

            cq = CommandsQueue.Dequeue();

            if (cq.Status != Answer.Statuses.Wait)
            {
                return;
            }

            var deviceCommand = cq.Command.Deserialize<DeviceCommand>();

            if(deviceCommand == null)
            {
                throw new NullReferenceException();
            }

            var d = GetDeviceById(deviceCommand.DeviceId);

            //для ККМ
            if (d.Type == Enums.DeviceTypes.FiscalRegistrar)
            {
                var manager = new KkmCommandsManager();
                manager.Do(cq);
            }
        }
        catch (Exception e)
        {
            if (cq != null)
            {
                cq.Status = Answer.Statuses.Error;
                cq.Result = JsonSerializer.SerializeToNode(new ErrorObject(e));
            }
            
            Console.WriteLine(e);
          
        }
    }

    /// <summary>
    /// Установка результата для команды
    /// </summary>
    /// <param name="action">Вызываемый метод</param>
    /// <param name="cq">Объект очереди</param>
    public static void SetResult(Action action, CommandQueue cq)
    {
       
            action();
            cq.Status = Answer.Statuses.Ok;
       
    }

    /// <summary>
    /// Установка результата выполнения команды
    /// </summary>
    /// <typeparam name="T">Тип возвращаемого объекта вызовом команды</typeparam>
    /// <param name="func">Вызываемый метод</param>
    /// <param name="cq">Объект очереди</param>
    public static void SetResult<T>(Func<T> func, CommandQueue cq)
    {
       
            var result = func.Invoke();
            cq.Status = Answer.Statuses.Ok;
            cq.Result = JsonSerializer.SerializeToNode(result);
      



     

    }

    /// <summary>
    /// Получить устройство по ИД
    /// </summary>
    /// <param name="id">ИД устройства</param>
    /// <returns></returns>
    public static Device GetDeviceById(string id)
    {
        var cr = new ConfigRepository();
        var c = cr.Get();

        var d = c.Devices.Single(x => x.Id == id);


        return d;
    }
}