#include "pch.h"

// See Ripcord.Media.Interop/dllmain.cpp and dllmain.def for why this file exists and doesn't use
// __declspec(dllexport).
extern std::int32_t __stdcall WINRT_CanUnloadNow() noexcept;
extern std::int32_t __stdcall WINRT_GetActivationFactory(void* classId, void** factory) noexcept;

extern "C" HRESULT __stdcall DllGetActivationFactory(void* classId, void** factory)
{
    return static_cast<HRESULT>(WINRT_GetActivationFactory(classId, factory));
}

// _Use_decl_annotations_ adopts the SAL contract combaseapi.h already declares for this export.
// Without it code analysis reports C28251 (inconsistent annotation) - a real mismatch, if a harmless
// one, and worth silencing at the source rather than by suppressing the check.
_Use_decl_annotations_
extern "C" HRESULT __stdcall DllCanUnloadNow()
{
    return static_cast<HRESULT>(WINRT_CanUnloadNow());
}
