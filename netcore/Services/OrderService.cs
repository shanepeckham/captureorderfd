namespace OrderCaptureAPI.Services
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using MongoDB.Driver;
    using OrderCaptureAPI.Models;
    using OrderCaptureAPI.Singetons;
    using Microsoft.ApplicationInsights;
    using Amqp;
    using Amqp.Framing;
    using MongoDB.Bson;
    using System.Text;

    public class OrderService
    {

        #region Protected variables
        private string _teamName;
        private IMongoCollection<Order> ordersCollection;
        private readonly ILogger _logger;
        private readonly TelemetryClient _telemetryClient;
        private readonly TelemetryClient _customTelemetryClient;
        private bool _isCosmosDb;
        private bool _isEventHub;
        #endregion

        #region Constructor
        public OrderService(ILoggerFactory loggerFactory, TelemetryClient telemetryClient)
        {
            // Initialize the class logger and telemetry client
            _logger = loggerFactory.CreateLogger("OrderService");
            _telemetryClient = telemetryClient;

            // Initialize custom telemetry client, if the key is provided
            var customInsightsKey = System.Environment.GetEnvironmentVariable("APPINSIGHTS_KEY");
            if (!string.IsNullOrEmpty(customInsightsKey))
            {
                _customTelemetryClient = new TelemetryClient();
                _customTelemetryClient.InstrumentationKey = customInsightsKey;
            }

            // Initialize the class using environment variables
            _teamName = System.Environment.GetEnvironmentVariable("TEAMNAME");

            // Initialize MongoDB
            // Figure out if this is running on CosmosDB
            var mongoURL = MongoClientSingleton.Instance.Settings.Server.ToString();
            _isCosmosDb = mongoURL.Contains("documents.azure.com");

            // Initialize AMQP
            var amqpURL = System.Environment.GetEnvironmentVariable("AMQPURL");
            _isEventHub = amqpURL.Contains("servicebus.windows.net");

            // Log out the env variables
            ValidateVariable(customInsightsKey, "APPINSIGHTS_KEY");
            ValidateVariable(mongoURL, "MONGOURL");
            _logger.LogInformation($"Cosmos DB: {_isCosmosDb}");
            ValidateVariable(amqpURL, "AMQPURL");
            _logger.LogInformation($"Event Hub: {_isEventHub}");
            ValidateVariable(_teamName, "TEAMNAME");
        }
        #endregion

        #region Methods

        // Logs out value of a variable
        public void ValidateVariable(string value, string envName)
        {
            if (string.IsNullOrEmpty(value))
                _logger.LogInformation($"The environment variable {envName} has not been set");
            else
                _logger.LogInformation($"The environment variable {envName} is {value}");
        }
        public async Task<string> AddOrderToMongoDB(Order order)
        {

            var startTime = DateTime.UtcNow;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var success = false;
            try
            {
                // Get the MongoDB collection
                ordersCollection = MongoClientSingleton.Instance.GetDatabase("k8orders").GetCollection<Order>("orders");
                order.Status = "Open";

                if (string.IsNullOrEmpty(order.Source))
                {
                    order.Source = System.Environment.GetEnvironmentVariable("SOURCE");
                }

                var rnd = new Random(DateTime.Now.Millisecond);
                int partition = rnd.Next(11);
                order.Product = $"product-{partition}";

                await ordersCollection.InsertOneAsync(order);

                var db = _isCosmosDb ? "CosmosDB" : "MongoDB";
                await Task.Run(() =>
                {
                    _telemetryClient.TrackEvent($"CapureOrder: - Team Name {_teamName} -  db {db}");
                });
                _logger.LogTrace($"CapureOrder {order.Id}: - Team Name {_teamName} -  db {db}");
                success = true;
                return order.Id;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, ex.Message, order);
                if(_customTelemetryClient!=null)
                    _customTelemetryClient.TrackException(ex);
                throw;
            }
            finally
            {
                if(_customTelemetryClient!=null) {
                    if (_isCosmosDb)
                        _customTelemetryClient.TrackDependency($"CosmosDB", MongoClientSingleton.Instance.Settings.Server.ToString(), "MongoDB", "", startTime, timer.Elapsed, success ? "200" : "500", success);
                        
                    else
                        _customTelemetryClient.TrackDependency($"MongoDB", MongoClientSingleton.Instance.Settings.Server.ToString(), "MongoDB", "", startTime, timer.Elapsed, success ? "200" : "500", success);
                }
            }
        }

        public async Task AddOrderToAMQP(Order order)
        {
            if (_isEventHub)
                await AddOrderToAMQP10(order);
            else
            {
                await AddOrderToAMQP091(order);
            }
        }

        private async Task AddOrderToAMQP10(Order order)
        {
            var startTime = DateTime.UtcNow;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var success = false;
            try
            {
                // Send to AMQP
                var amqpMessage = new Message($"{{'order': '{order.Id}', 'source': '{_teamName}'}}");
                await AMQP10ClientSingleton.Instance.SendAsync(amqpMessage);
                _logger.LogTrace($"Sent message to AMQP 1.0 (EventHub) {AMQP10ClientSingleton.AMQPUrl} {amqpMessage.ToJson()}");
                success = true;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, ex.Message, order);
                if(_customTelemetryClient!=null)
                    _customTelemetryClient.TrackException(ex);
                throw;
            }
            finally
            {
                if(_customTelemetryClient!=null)
                    _customTelemetryClient.TrackDependency($"AMQP-EventHub", AMQP10ClientSingleton.AMQPUrl, "AMQP", "", startTime, timer.Elapsed, success ? "200" : "500", success);
            }
        }

        private async Task AddOrderToAMQP091(Order order)
        {
            var startTime = DateTime.UtcNow;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var success = false;
            try
            {
                await Task.Run(() =>
                {
                    // Send to AMQP
                    var connection = AMQP091ClientSingleton.AMQPConnectionFactory.CreateConnection();

                    using (var channel = connection.CreateModel())
                    {
                        channel.QueueDeclare(
                            queue: "order",
                            durable: true,
                            exclusive: false,
                            autoDelete: false,
                            arguments: null);

                        var amqpMessage = $"{{'order': '{order.Id}', 'source': '{_teamName}'}}";
                        var body = Encoding.UTF8.GetBytes(amqpMessage);

                        channel.BasicPublish(
                            exchange: "",
                            mandatory:false,
                            routingKey: "order",
                            basicProperties: null,
                            body: body);

                        _logger.LogTrace($"Sent message to AMQP 0.9.1 (RabbitMQ) {AMQP091ClientSingleton.AMQPUrl} {amqpMessage}");
                    }
                });

                success = true;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, ex.Message, order);
                if(_customTelemetryClient!=null)
                    _customTelemetryClient.TrackException(ex);
                throw;
            }
            finally
            {
                if(_customTelemetryClient!=null)
                    _customTelemetryClient.TrackDependency($"AMQP-RabbitMQ", AMQP091ClientSingleton.AMQPUrl, "AMQP", "", startTime, timer.Elapsed, success ? "200" : "500", success);                    
            }
        }
        #endregion
    }
}