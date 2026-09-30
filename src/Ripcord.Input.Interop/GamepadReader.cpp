#include "pch.h"
#include "GamepadReader.h"
#include "Ripcord.Input.Interop.GamepadReader.g.cpp"

#include <algorithm>

using namespace GameInput::v3;
using Microsoft::WRL::ComPtr;

namespace winrt::Ripcord::Input::Interop::implementation
{
    GamepadReader::GamepadReader()
    {
        // Intentionally not checking the HRESULT beyond this: no gamepad being present, or the GameInput runtime
        // being unavailable, is a normal condition (no hardware attached to a dev/CI machine) -
        // GetConnectedStates() below reports no pads rather than throwing.
        if (FAILED(GameInputCreate(m_gameInput.GetAddressOf())) || !m_gameInput)
        {
            m_gameInput.Reset();
            return;
        }

        // Blocking enumeration: the pads already attached are reported through the callback before this call
        // returns, so the first poll sees them. Later connects and disconnects arrive on GameInput's own thread.
        if (FAILED(m_gameInput->RegisterDeviceCallback(
                nullptr,
                GameInputKindGamepad,
                GameInputDeviceConnected,
                GameInputBlockingEnumeration,
                this,
                &GamepadReader::OnDeviceChanged,
                &m_deviceCallback)))
        {
            m_deviceCallback = 0;
        }
    }

    GamepadReader::~GamepadReader()
    {
        // The callback's context is this object, so it must be unregistered before the object goes. StopCallback
        // first so no new callback starts, then unregister. Not under m_lock: a callback in flight takes it.
        if (m_gameInput && m_deviceCallback != 0)
        {
            m_gameInput->StopCallback(m_deviceCallback);
            m_gameInput->UnregisterCallback(m_deviceCallback);
            m_deviceCallback = 0;
        }
    }

    void CALLBACK GamepadReader::OnDeviceChanged(
        GameInputCallbackToken,
        void* context,
        IGameInputDevice* device,
        uint64_t,
        GameInputDeviceStatus currentStatus,
        GameInputDeviceStatus)
    {
        static_cast<GamepadReader*>(context)->Track(device, (currentStatus & GameInputDeviceConnected) != 0);
    }

    void GamepadReader::Track(IGameInputDevice* device, bool connected)
    {
        if (!device)
        {
            return;
        }

        std::lock_guard lock{ m_lock };
        auto existing = std::find_if(m_devices.begin(), m_devices.end(),
            [device](const TrackedDevice& tracked) { return tracked.Device.Get() == device; });

        if (connected && existing == m_devices.end())
        {
            m_devices.push_back({ ComPtr<IGameInputDevice>(device), m_nextId++ });
        }
        else if (!connected && existing != m_devices.end())
        {
            m_devices.erase(existing);
        }
    }

    Windows::Foundation::Collections::IVectorView<Ripcord::Input::Interop::GamepadState>
        GamepadReader::GetConnectedStates()
    {
        std::vector<Ripcord::Input::Interop::GamepadState> states;
        if (!m_gameInput)
        {
            return winrt::single_threaded_vector(std::move(states)).GetView();
        }

        // Copy the list out rather than reading under the lock, so a connect arriving mid-poll never waits on
        // the reads below.
        std::vector<TrackedDevice> devices;
        {
            std::lock_guard lock{ m_lock };
            devices = m_devices;
        }

        for (const TrackedDevice& tracked : devices)
        {
            // Each device named explicitly. Passing nullptr here was the bug: it means "whichever gamepad
            // reported last", which a Bluetooth DualSense wins essentially every time.
            ComPtr<IGameInputReading> reading;
            if (FAILED(m_gameInput->GetCurrentReading(
                    GameInputKindGamepad, tracked.Device.Get(), reading.GetAddressOf())))
            {
                continue;
            }

            GameInputGamepadState state{};
            if (!reading->GetGamepadState(&state))
            {
                continue;
            }

            Ripcord::Input::Interop::GamepadState result{};
            result.DeviceId = tracked.Id;
            result.Buttons = static_cast<uint64_t>(state.buttons);
            result.LeftTrigger = state.leftTrigger;
            result.RightTrigger = state.rightTrigger;
            result.LeftThumbstickX = state.leftThumbstickX;
            result.LeftThumbstickY = state.leftThumbstickY;
            result.RightThumbstickX = state.rightThumbstickX;
            result.RightThumbstickY = state.rightThumbstickY;
            result.TimestampTicks = reading->GetTimestamp();
            states.push_back(result);
        }

        return winrt::single_threaded_vector(std::move(states)).GetView();
    }
}
