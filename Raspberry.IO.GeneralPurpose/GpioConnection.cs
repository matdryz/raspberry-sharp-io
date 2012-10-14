#region References

using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Threading;
using Raspberry.IO.GeneralPurpose.Configuration;

#endregion

namespace Raspberry.IO.GeneralPurpose
{
    /// <summary>
    /// Represents a connection to the GPIO pins.
    /// </summary>
    public class GpioConnection : IDisposable
    {
        #region Fields

        private readonly Dictionary<ProcessorPin, PinConfiguration> pinConfigurations;
        private readonly Dictionary<string, PinConfiguration> namedPins;

        private readonly Timer timer;
        private readonly Dictionary<ProcessorPin, bool> pinValues = new Dictionary<ProcessorPin, bool>();
        private readonly Dictionary<ProcessorPin, EventHandler<PinStatusEventArgs>> pinEvents = new Dictionary<ProcessorPin, EventHandler<PinStatusEventArgs>>();
        private readonly Dictionary<ProcessorPin, bool> pinRawValues = new Dictionary<ProcessorPin, bool>();

        /// <summary>
        /// Gets the default blink duration, in milliseconds.
        /// </summary>
        public const int DefaultBlinkDuration = 250;

        #endregion

        #region Instance Management

        /// <summary>
        /// Initializes a new instance of the <see cref="GpioConnection"/> class.
        /// </summary>
        /// <param name="pins">The pins.</param>
        public GpioConnection(params PinConfiguration[] pins) : this(true, null, (IEnumerable<PinConfiguration>) pins){}

        /// <summary>
        /// Initializes a new instance of the <see cref="GpioConnection"/> class.
        /// </summary>
        /// <param name="pins">The pins.</param>
        public GpioConnection(IEnumerable<PinConfiguration> pins) : this(true, null, pins){}

        /// <summary>
        /// Initializes a new instance of the <see cref="GpioConnection"/> class.
        /// </summary>
        /// <param name="driver">The driver.</param>
        /// <param name="pins">The pins.</param>
        public GpioConnection(IConnectionDriver driver, params PinConfiguration[] pins) : this(true, driver, (IEnumerable<PinConfiguration>) pins){}

        /// <summary>
        /// Initializes a new instance of the <see cref="GpioConnection"/> class.
        /// </summary>
        /// <param name="driver">The driver.</param>
        /// <param name="pins">The pins.</param>
        public GpioConnection(IConnectionDriver driver, IEnumerable<PinConfiguration> pins) : this(true, driver, pins){}

        /// <summary>
        /// Initializes a new instance of the <see cref="GpioConnection"/> class.
        /// </summary>
        /// <param name="open">if set to <c>true</c>, connection is opened on creation.</param>
        /// <param name="pins">The pins.</param>
        public GpioConnection(bool open, params PinConfiguration[] pins) : this(open, null, (IEnumerable<PinConfiguration>) pins){}

        /// <summary>
        /// Initializes a new instance of the <see cref="GpioConnection"/> class.
        /// </summary>
        /// <param name="open">if set to <c>true</c>, connection is opened on creation.</param>
        /// <param name="driver">The driver.</param>
        /// <param name="pins">The pins.</param>
        public GpioConnection(bool open, IConnectionDriver driver, params PinConfiguration[] pins) : this(open, driver, (IEnumerable<PinConfiguration>) pins){}

        /// <summary>
        /// Initializes a new instance of the <see cref="GpioConnection"/> class.
        /// </summary>
        /// <param name="open">if set to <c>true</c>, connection is opened on creation.</param>
        /// <param name="pins">The pins.</param>
        public GpioConnection(bool open, IEnumerable<PinConfiguration> pins) : this(open, null, pins){}

        /// <summary>
        /// Initializes a new instance of the <see cref="GpioConnection"/> class.
        /// </summary>
        /// <param name="open">if set to <c>true</c>, connection is opened on creation.</param>
        /// <param name="driver">The driver.</param>
        /// <param name="pins">The pins.</param>
        public GpioConnection(bool open, IConnectionDriver driver, IEnumerable<PinConfiguration> pins)
        {
            Driver = driver ?? GetDefaultDriver();
            Pins = new ConnectedPins(this);

            var pinList = pins.ToList();
            pinConfigurations = pinList.ToDictionary(p => p.Pin);

            namedPins = pinList.Where(p => !string.IsNullOrEmpty(p.Name)).ToDictionary(p => p.Name);

            timer = new Timer(CheckInputPins, null, Timeout.Infinite, Timeout.Infinite);
            if (open)
                Open();
        }

        void IDisposable.Dispose()
        {
            Close();
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets a value indicating whether connection is opened.
        /// </summary>
        /// <value>
        ///   <c>true</c> if connection is opened; otherwise, <c>false</c>.
        /// </value>
        public bool IsOpened { get; private set; }

        /// <summary>
        /// Gets the driver.
        /// </summary>
        public IConnectionDriver Driver { get; private set; }

        /// <summary>
        /// Gets or sets the status of the pin having the specified name.
        /// </summary>
        public bool this[string name]
        {
            get { return this[namedPins[name].Pin]; }
            set { this[namedPins[name].Pin] = value; }
        }

        /// <summary>
        /// Gets or sets the status of the specified pin.
        /// </summary>
        public bool this[ConnectorPin pin]
        {
            get { return this[pin.ToProcessor()]; }
            set { this[pin.ToProcessor()] = value; }
        }

        /// <summary>
        /// Gets or sets the status of the specified pin.
        /// </summary>
        public bool this[PinConfiguration pin]
        {
            get { return pinValues[pin.Pin]; }
            set
            {
                if (pin.Direction == PinDirection.Output)
                {
                    var pinValue = pin.GetEffective(value);
                    Driver.Write(pin.Pin, pinValue);

                    pinValues[pin.Pin] = value;
                    OnPinStatusChanged(new PinStatusEventArgs {Enabled = value, Configuration = pin});
                }
                else
                    throw new InvalidOperationException("Input pin value cannot be set");

            }
        }

        /// <summary>
        /// Gets or sets the status of the specified pin.
        /// </summary>
        public bool this[ProcessorPin pin]
        {
            get { return this[pinConfigurations[pin]]; }
            set { this[pinConfigurations[pin]] = value; }
        }

        /// <summary>
        /// Gets the pins.
        /// </summary>
        public ConnectedPins Pins { get; private set; }

        #endregion

        #region Methods

        /// <summary>
        /// Opens the connection.
        /// </summary>
        public void Open()
        {
            lock (timer)
            {
                if (IsOpened)
                    return;

                foreach (var pin in pinConfigurations.Values)
                    Export(pin);

                timer.Change(250, 50);
                IsOpened = true;
            }
        }

        /// <summary>
        /// Closes the connection.
        /// </summary>
        public void Close()
        {
            lock (timer)
            {
                if (!IsOpened)
                    return;

                timer.Dispose();
                foreach (var pin in pinConfigurations.Values)
                    Unexport(pin);

                IsOpened = false;
            }
        }

        /// <summary>
        /// Clears pin attached to this connection.
        /// </summary>
        public void Clear()
        {
            lock (pinConfigurations)
            {
                foreach (var pinConfiguration in pinConfigurations.Values)
                    Unexport(pinConfiguration);

                pinConfigurations.Clear();
                namedPins.Clear();
                pinRawValues.Clear();
                pinValues.Clear();
            }
        }

        /// <summary>
        /// Adds the specified pin.
        /// </summary>
        /// <param name="pin">The pin.</param>
        public void Add(PinConfiguration pin)
        {
            lock (pinConfigurations)
            {
                if (pinConfigurations.ContainsKey(pin.Pin))
                    throw new InvalidOperationException("This pin is already present on the connection");
                if (!string.IsNullOrEmpty(pin.Name) && namedPins.ContainsKey(pin.Name))
                    throw new InvalidOperationException("A pin with the same name is already present on the connection");

                pinConfigurations.Add(pin.Pin, pin);

                if (!string.IsNullOrEmpty(pin.Name))
                    namedPins.Add(pin.Name, pin);

                Export(pin);
            }
        }

        /// <summary>
        /// Determines whether the connection contains the specified pin.
        /// </summary>
        /// <param name="pinName">Name of the pin.</param>
        /// <returns>
        ///   <c>true</c> if the connection contains the specified pin; otherwise, <c>false</c>.
        /// </returns>
        public bool Contains(string pinName)
        {
            return namedPins.ContainsKey(pinName);
        }

        /// <summary>
        /// Determines whether the connection contains the specified pin.
        /// </summary>
        /// <param name="pin">The pin.</param>
        /// <returns>
        ///   <c>true</c> if the connection contains the specified pin; otherwise, <c>false</c>.
        /// </returns>
        public bool Contains(ConnectorPin pin)
        {
            return pinConfigurations.ContainsKey(pin.ToProcessor());
        }

        /// <summary>
        /// Determines whether the connection contains the specified pin.
        /// </summary>
        /// <param name="pin">The pin.</param>
        /// <returns>
        ///   <c>true</c> if the connection contains the specified pin; otherwise, <c>false</c>.
        /// </returns>
        public bool Contains(ProcessorPin pin)
        {
            return pinConfigurations.ContainsKey(pin);
        }

        /// <summary>
        /// Determines whether the connection contains the specified pin.
        /// </summary>
        /// <param name="configuration">The pin configuration.</param>
        /// <returns>
        ///   <c>true</c> if the connection contains the specified pin; otherwise, <c>false</c>.
        /// </returns>
        public bool Contains(PinConfiguration configuration)
        {
            return pinConfigurations.ContainsKey(configuration.Pin);
        }

        /// <summary>
        /// Removes the specified pin.
        /// </summary>
        /// <param name="pinName">Name of the pin.</param>
        public void Remove(string pinName)
        {
            Remove(namedPins[pinName]);
        }

        /// <summary>
        /// Removes the specified pin.
        /// </summary>
        /// <param name="pin">The pin.</param>
        public void Remove(ConnectorPin pin)
        {
            Remove(pinConfigurations[pin.ToProcessor()]);
        }

        /// <summary>
        /// Removes the specified pin.
        /// </summary>
        /// <param name="pin">The pin.</param>
        public void Remove(ProcessorPin pin)
        {
            Remove(pinConfigurations[pin]);
        }

        /// <summary>
        /// Removes the specified pin.
        /// </summary>
        /// <param name="configuration">The pin configuration.</param>
        public void Remove(PinConfiguration configuration)
        {
            lock (pinConfigurations)
            {
                Unexport(configuration);

                pinConfigurations.Remove(configuration.Pin);
                if (!string.IsNullOrEmpty(configuration.Name))
                    namedPins.Remove(configuration.Name);

                pinRawValues.Remove(configuration.Pin);
                pinValues.Remove(configuration.Pin);
            }
        }

        /// <summary>
        /// Toggles the specified pin.
        /// </summary>
        /// <param name="pinName">Name of the pin.</param>
        public void Toggle(string pinName)
        {
            this[pinName] = !this[pinName];
        }

        /// <summary>
        /// Toggles the specified pin.
        /// </summary>
        /// <param name="pin">The pin.</param>
        public void Toggle(ProcessorPin pin)
        {
            this[pin] = !this[pin];
        }

        /// <summary>
        /// Toggles the specified pin.
        /// </summary>
        /// <param name="pin">The pin.</param>
        public void Toggle(ConnectorPin pin)
        {
            this[pin] = !this[pin];
        }

        /// <summary>
        /// Toggles the specified pin.
        /// </summary>
        /// <param name="configuration">The pin configuration.</param>
        public void Toggle(PinConfiguration configuration)
        {
            this[configuration] = !this[configuration];
        }

        /// <summary>
        /// Blinks the specified pin.
        /// </summary>
        /// <param name="pinName">Name of the pin.</param>
        /// <param name="duration">The duration, in millisecond.</param>
        public void Blink(string pinName, int duration = DefaultBlinkDuration)
        {
            Toggle(pinName);
            Thread.Sleep(duration);
            Toggle(pinName);
        }

        /// <summary>
        /// Blinks the specified pin.
        /// </summary>
        /// <param name="pin">The pin.</param>
        /// <param name="duration">The duration, in millisecond.</param>
        public void Blink(ProcessorPin pin, int duration = DefaultBlinkDuration)
        {
            Toggle(pin);
            Thread.Sleep(duration);
            Toggle(pin);
        }

        /// <summary>
        /// Blinks the specified pin.
        /// </summary>
        /// <param name="pin">The pin.</param>
        /// <param name="duration">The duration, in millisecond.</param>
        public void Blink(ConnectorPin pin, int duration = DefaultBlinkDuration)
        {
            Toggle(pin);
            Thread.Sleep(duration);
            Toggle(pin);
        }

        /// <summary>
        /// Blinks the specified pin.
        /// </summary>
        /// <param name="configuration">The pin configuration.</param>
        /// <param name="duration">The duration, in millisecond.</param>
        public void Blink(PinConfiguration configuration, int duration = DefaultBlinkDuration)
        {
            Toggle(configuration);
            Thread.Sleep(duration);
            Toggle(configuration);
        }

        #endregion

        #region Events

        /// <summary>
        /// Occurs when the status of a pin changed.
        /// </summary>
        public event EventHandler<PinStatusEventArgs> PinStatusChanged;

        #endregion

        #region Protected Methods

        /// <summary>
        /// Raises the <see cref="PinStatusChanged"/> event.
        /// </summary>
        /// <param name="e">The <see cref="Raspberry.IO.GeneralPurpose.PinStatusEventArgs"/> instance containing the event data.</param>
        protected void OnPinStatusChanged(PinStatusEventArgs e)
        {
            var handler = PinStatusChanged;
            if (handler != null)
                handler(this, e);
        }

        #endregion

        #region Private Helpers

        private IEnumerable<PinConfiguration> Configurations
        {
            get { return pinConfigurations.Values; }
        }

        private static IConnectionDriver GetDefaultDriver()
        {
            var configurationSection = ConfigurationManager.GetSection("gpioConnection") as GpioConnectionConfigurationSection;
            if (configurationSection != null && !string.IsNullOrEmpty(configurationSection.DriverTypeName))
                return (IConnectionDriver) Activator.CreateInstance(Type.GetType(configurationSection.DriverTypeName, true));
            else
                return new MemoryConnectionDriver();
        }

        private void Export(PinConfiguration configuration)
        {
            if (configuration.StatusChangedAction != null)
            {
                var handler = new EventHandler<PinStatusEventArgs>((sender, args) =>
                                                                       {
                                                                           if (args.Configuration == configuration)
                                                                               configuration.StatusChangedAction(args.Enabled);
                                                                       });
                pinEvents[configuration.Pin] = handler;
                PinStatusChanged += handler;
            }

            Driver.Allocate(configuration.Pin, configuration.Direction);
            var outputConfiguration = configuration as OutputPinConfiguration;
            if (outputConfiguration != null)
                this[configuration.Pin] = outputConfiguration.GetEffective(outputConfiguration.Enabled);
            else
            {
                var pinValue = Driver.Read(configuration.Pin);
                pinRawValues[configuration.Pin] = pinValue;

                var switchConfiguration = configuration as SwitchInputPinConfiguration;
                if (switchConfiguration != null)
                {
                    pinValues[configuration.Pin] = switchConfiguration.Enabled;
                    OnPinStatusChanged(new PinStatusEventArgs {Configuration = configuration, Enabled = pinValues[configuration.Pin]});
                }
                else
                {
                    pinValues[configuration.Pin] = configuration.GetEffective(pinValue);
                    OnPinStatusChanged(new PinStatusEventArgs { Configuration = configuration, Enabled = pinValues[configuration.Pin] });
                }
            }
        }

        private void Unexport(PinConfiguration configuration)
        {
            if (configuration.Direction == PinDirection.Output)
            {
                Driver.Write(configuration.Pin, false);
                OnPinStatusChanged(new PinStatusEventArgs { Enabled = false, Configuration = configuration });
            }

            Driver.Release(configuration.Pin);

            EventHandler<PinStatusEventArgs> handler;
            if (pinEvents.TryGetValue(configuration.Pin, out handler))
            {
                PinStatusChanged -= handler;
                pinEvents.Remove(configuration.Pin);
            }
        }

        private void CheckInputPins(object state)
        {
            Dictionary<ProcessorPin, bool> newPinValues;

            lock (pinConfigurations)
            {
                newPinValues = pinConfigurations.Values
                    .Where(p => p.Direction == PinDirection.Input)
                    .Select(p => new {p.Pin, Value = Driver.Read(p.Pin)})
                    .ToDictionary(p => p.Pin, p => p.Value);
            }

            foreach (var np in newPinValues)
            {
                var oldPinValue = pinRawValues[np.Key];
                var newPinValue = np.Value;

                pinRawValues[np.Key] = newPinValue;
                if (oldPinValue != newPinValue)
                {
                    var pin = (InputPinConfiguration) pinConfigurations[np.Key];
                    var switchPin = pin as SwitchInputPinConfiguration;

                    if (switchPin != null)
                    {
                        if (pin.GetEffective(newPinValue))
                        {
                            pinValues[np.Key] = !pinValues[np.Key];
                            OnPinStatusChanged(new PinStatusEventArgs {Configuration = pin, Enabled = pinValues[np.Key]});
                        }
                    }
                    else
                    {
                        pinValues[np.Key] = pin.GetEffective(newPinValue);
                        OnPinStatusChanged(new PinStatusEventArgs {Configuration = pin, Enabled = pinValues[np.Key]});
                    }
                }
            }
        }

        private PinConfiguration GetConfiguration(string pinName)
        {
            return namedPins[pinName];
        }

        private PinConfiguration GetConfiguration(ProcessorPin pin)
        {
            return pinConfigurations[pin];
        }

        #endregion

        #region Inner Classes

        /// <summary>
        /// Represents a connected pin.
        /// </summary>
        public class ConnectedPin
        {
            private readonly GpioConnection connection;
            private readonly HashSet<EventHandler<PinStatusEventArgs>> events = new HashSet<EventHandler<PinStatusEventArgs>>();

            /// <summary>
            /// Initializes a new instance of the <see cref="ConnectedPin"/> class.
            /// </summary>
            /// <param name="connection">The connection.</param>
            /// <param name="pinConfiguration">The pin configuration.</param>
            public ConnectedPin(GpioConnection connection, PinConfiguration pinConfiguration)
            {
                this.connection = connection;
                Configuration = pinConfiguration;
            }

            /// <summary>
            /// Gets the configuration.
            /// </summary>
            public PinConfiguration Configuration { get; private set; }

            /// <summary>
            /// Gets or sets a value indicating whether this <see cref="ConnectedPin"/> is enabled.
            /// </summary>
            /// <value>
            ///   <c>true</c> if enabled; otherwise, <c>false</c>.
            /// </value>
            public bool Enabled
            {
                get { return connection[Configuration]; }
                set { connection[Configuration] = value; }
            }

            /// <summary>
            /// Toggles this pin.
            /// </summary>
            public void Toggle()
            {
                connection.Toggle(Configuration);
            }

            /// <summary>
            /// Blinks the pin.
            /// </summary>
            /// <param name="duration">The blink duration, in millisecond.</param>
            public void Blink(int duration = DefaultBlinkDuration)
            {
                connection.Blink(Configuration, duration);
            }

            /// <summary>
            /// Occurs when pin status changed.
            /// </summary>
            public event EventHandler<PinStatusEventArgs> StatusChanged
            {
                add
                {
                    if (events.Count == 0)
                        connection.PinStatusChanged += ConnectionPinStatusChanged;
                    events.Add(value);
                }
                remove
                {
                    events.Remove(value);
                    if (events.Count == 0)
                        connection.PinStatusChanged -= ConnectionPinStatusChanged;
                }
            }

            private void ConnectionPinStatusChanged(object sender, PinStatusEventArgs pinStatusEventArgs)
            {
                if (pinStatusEventArgs.Configuration.Pin != Configuration.Pin)
                    return;

                foreach (var eventHandler in events)
                    eventHandler(sender, pinStatusEventArgs);
            }
        }

        /// <summary>
        /// Represents connected pins.
        /// </summary>
        public class ConnectedPins : IEnumerable<ConnectedPin>
        {
            private readonly GpioConnection connection;

            internal ConnectedPins(GpioConnection connection)
            {
                this.connection = connection;
            }

            /// <summary>
            /// Gets the status of the specified pin.
            /// </summary>
            public ConnectedPin this[ProcessorPin pin]
            {
                get { return new ConnectedPin(connection, connection.GetConfiguration(pin)); }
            }

            /// <summary>
            /// Gets the status of the specified pin.
            /// </summary>
            public ConnectedPin this[string name]
            {
                get { return new ConnectedPin(connection, connection.GetConfiguration(name)); }
            }

            /// <summary>
            /// Gets the status of the specified pin.
            /// </summary>
            public ConnectedPin this[ConnectorPin pin]
            {
                get { return this[pin.ToProcessor()]; }
            }

            /// <summary>
            /// Gets the status of the specified pin.
            /// </summary>
            public ConnectedPin this[PinConfiguration pin]
            {
                get { return new ConnectedPin(connection, pin); }
            }

            /// <summary>
            /// Returns an enumerator that iterates through a collection.
            /// </summary>
            /// <returns>
            /// An <see cref="T:System.Collections.IEnumerator"/> object that can be used to iterate through the collection.
            /// </returns>
            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            /// <summary>
            /// Gets the enumerator.
            /// </summary>
            /// <returns>The enumerator.</returns>
            public IEnumerator<ConnectedPin> GetEnumerator()
            {
                return connection.Configurations.Select(c => new ConnectedPin(connection, c)).GetEnumerator();
            }
        }

        #endregion
    }
}