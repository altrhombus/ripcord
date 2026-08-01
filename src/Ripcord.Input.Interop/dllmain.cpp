#include "pch.h"

// See Ripcord.Media.Interop/dllmain.cpp and dllmain.def for why this file exists and doesn't use
// __declspec(dllexport).
extern std::int32_t __stdcall WINRT_CanUnloadNow() noexcept;
extern std::int32_t __stdcall WINRT_GetActivationFactory(void* classId, void** factory) noexcept;

extern "C" HRESULT __stdcall DllGetActivationFactory(void* classId, void** factory)
{
    return static_cast<HRESULT>(WINRT_GetActivationFactory(classId, factory));
}

extern "C" HRESULT __stdcall DllCanUnloadNow()
{
    return static_cast<HRESULT>(WINRT_CanUnloadNow());
}
