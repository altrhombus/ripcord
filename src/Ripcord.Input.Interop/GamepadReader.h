#pragma once
#include "Ripcord.Input.Interop.GamepadReader.g.h"

#include <mutex>
#include <vector>
#include <wrl/client.h>
#include <GameInput.h>

namespace winrt::Ripcord::Input::Interop::implementation
{
    struct GamepadReader : GamepadReaderT<GamepadReader>
    {
        GamepadReader();
        ~GamepadReader();

        Windows::Foundation::Collections::IVectorView<Ripcord::Input::Interop::GamepadState> GetConnectedStates();

        static bool IsRuntimeAvailable();

    private:
        struct TrackedDevice
        {
            Microsoft::WRL::ComPtr<GameInput::v3::IGameInputDevice> Device;
            uint64_t Id;
            uint16_t VendorId;
            uint16_t ProductId;
        };

        static void CALLBACK OnDeviceChanged(
            GameInput::v3::GameInputCallbackToken token,
            void* context,
            GameInput::v3::IGameInputDevice* device,
            uint64_t timestamp,
            GameInput::v3::GameInputDeviceStatus currentStatus,
            GameInput::v3::GameInputDeviceStatus previousStatus);

        void Track(GameInput::v3::IGameInputDevice* device, bool connected);

        Microsoft::WRL::ComPtr<GameInput::v3::IGameInput> m_gameInput;
        GameInput::v3::GameInputCallbackToken m_deviceCallback = 0;

        // Written from GameInput's callback thread, read from the poll thread.
        std::mutex m_lock;
        std::vector<TrackedDevice> m_devices;
        uint64_t m_nextId = 1;
    };
}

namespace winrt::Ripcord::Input::Interop::factory_implementation
{
    struct GamepadReader : GamepadReaderT<GamepadReader, implementation::GamepadReader>
    {
    };
}
