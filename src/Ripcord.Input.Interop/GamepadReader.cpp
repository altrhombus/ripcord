#include "pch.h"
#include "GamepadReader.h"
#include "Ripcord.Input.Interop.GamepadReader.g.cpp"

using namespace GameInput::v3;
using Microsoft::WRL::ComPtr;

namespace winrt::Ripcord::Input::Interop::implementation
{
    GamepadReader::GamepadReader()
    {
        // Intentionally not checking the HRESULT here: no gamepad being present, or the
        // GameInput runtime being unavailable, is a normal Phase-0 condition (no hardware
        // attached to the dev/CI machine) - GetLatestState() below reports IsConnected = false
        // rather than throwing.
        GameInputCreate(m_gameInput.GetAddressOf());
    }

    Ripcord::Input::Interop::GamepadState GamepadReader::GetLatestState()
    {
        Ripcord::Input::Interop::GamepadState result{};
        result.IsConnected = false;

        if (!m_gameInput)
        {
            return result;
        }

        ComPtr<IGameInputReading> reading;
        if (FAILED(m_gameInput->GetCurrentReading(GameInputKindGamepad, nullptr, reading.GetAddressOf())))
        {
            return result;
        }

        GameInputGamepadState state{};
        if (!reading->GetGamepadState(&state))
        {
            return result;
        }

        result.IsConnected = true;
        result.Buttons = static_cast<uint64_t>(state.buttons);
        result.LeftTrigger = state.leftTrigger;
        result.RightTrigger = state.rightTrigger;
        result.LeftThumbstickX = state.leftThumbstickX;
        result.LeftThumbstickY = state.leftThumbstickY;
        result.RightThumbstickX = state.rightThumbstickX;
        result.RightThumbstickY = state.rightThumbstickY;
        result.TimestampTicks = reading->GetTimestamp();
        return result;
    }
}
