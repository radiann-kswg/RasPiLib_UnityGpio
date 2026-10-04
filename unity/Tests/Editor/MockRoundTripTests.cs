// 模擬モードでの往復 1 本: 入力の edge が Changed に届き、I2C の応答が Awaitable で返ること。
using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace RasPiLib.UnityGpio.Tests
{
    public class MockRoundTripTests
    {
        [SetUp] public void Up() { UnityGpioSettings.instance.mode = UnityGpioSettings.Mode.Mock; }
        [TearDown] public void Down() { Gpio.Shutdown(); }

        static void PumpUntil(Func<bool> done, int ms = 2000)
        {
            var sw = Stopwatch.StartNew();
            while (!done() && sw.ElapsedMilliseconds < ms) { Gpio.Pump(); Thread.Sleep(5); }
            Gpio.Pump();
        }

        [Test]
        public void InputEdgeReachesChanged_AndI2cRoundTrips()
        {
            Assert.IsTrue(Gpio.IsAvailable);
            Assert.AreEqual("mock", Gpio.Board);
            CollectionAssert.AreEquivalent(new[] { 12, 13 }, Gpio.PwmPins);

            var button = Gpio.OpenInput(17, Bias.PullUp, Edge.Both);
            bool? got = null;
            button.Changed += v => got = v;
            PumpUntil(() => Gpio.Mock.Inputs.ContainsKey(17));
            Assert.IsTrue(button.Value, "プルアップなので初期値は High");

            Gpio.Mock.SetInput(17, false);
            PumpUntil(() => got.HasValue);
            Assert.AreEqual(false, got, "edge が Changed に届く");
            Assert.IsFalse(button.Value);

            Gpio.Mock.I2cHandler = (addr, write, read) => addr == 0x76 && write[0] == 0xD0 ? new byte[] { 0x60 } : new byte[read];
            var awaitable = Gpio.OpenI2c(1, 0x76).WriteReadAsync(new byte[] { 0xD0 }, 1);
            var awaiter = awaitable.GetAwaiter();
            PumpUntil(() => awaiter.IsCompleted);
            CollectionAssert.AreEqual(new byte[] { 0x60 }, awaiter.GetResult());

            var lamp = Gpio.OpenOutput(27);
            lamp.Value = true;                      // 送るだけ。例外が出ないこと
            new Servo(Gpio.OpenPwm(12, 50f)).Angle = 90f;
            button.Dispose();
            PumpUntil(() => !Gpio.Mock.Inputs.ContainsKey(17));
            Assert.IsFalse(Gpio.Mock.Inputs.ContainsKey(17), "release で模擬の入力も消える");
        }
    }
}
