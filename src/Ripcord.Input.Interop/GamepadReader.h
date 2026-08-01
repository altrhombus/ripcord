#pragma once
#include "Ripcord.Input.Interop.GamepadReader.g.h"

#include <wrl/client.h>
#include <GameInput.h>

namespace winrt::Ripcord::Input::Interop::implementation
{
    struct GamepadReader : GamepadReaderT<GamepadReader>
    {
        GamepadReader();

        Ripcord::Input::Interop::GamepadState GetLatestState();

    private:
        Microsoft::WRL::ComPtr<GameInput::v3::IGameInput> m_gameInput;
    };
}

namespace winrt::Ripcord::Input::Interop::factory_implementation
{
    struct GamepadReader : GamepadReaderT<GamepadReader, implementation::GamepadReader>
    {
    };
}
