#pragma once

#include <unknwn.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>

// Media Foundation (Layer 3 H.264 decode). The decoder MFT stays synchronous but decodes on the GPU (DXVA)
// when handed a D3D11 device manager — hence <d3d11_4.h>. The D3D11 device is purely MF's decode backend;
// the renderer/swap chain remain D3D12.
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mfobjects.h>
#include <mferror.h>
#include <wmcodecdsp.h>
#include <d3d11_4.h>
#include <vector>
#include <string>
#include <cstdio>   // swprintf_s for the decoder diagnostic string
