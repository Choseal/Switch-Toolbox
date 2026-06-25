// Global definitions and trivial stubs for symbols referenced by the Latte
// decompiler / FetchShader that have no real implementation in this standalone build.

#include "Cafe/HW/Latte/Core/LatteConst.h"
#include "Cafe/HW/Latte/ISA/LatteReg.h"
#include "Cafe/HW/Latte/Core/Latte.h"
#include "Cafe/HW/Latte/Renderer/Renderer.h"
#include "Cafe/HW/Latte/Core/LattePerformanceMonitor.h"

// memory base (decompiler never dereferences it; only FetchShader's unused FindByGPUState path does)
uint8* memory_base = nullptr;

uint8* memory_getPointerFromPhysicalOffset(uint32 physOffset)
{
	// Only reachable via LatteFetchShader::FindByGPUState(), which the decompiler does not call.
	return memory_base ? (memory_base + physOffset) : nullptr;
}

// renderer
std::unique_ptr<Renderer> g_renderer;

class StubRenderer : public Renderer
{
public:
	RendererAPI GetType() override { return RendererAPI::OpenGL; }
};

// GPU state
LatteGPUState_t LatteGPUState{};

// performance monitor
performanceMonitor_t performanceMonitor{};

// PPC timer (used by LattePerfStatTimer)
uint64 PPCTimer_getRawTsc() { return 0; }
uint64 PPCTimer_tscToMicroseconds(uint64 us) { return us; }

// Factory used by main.cpp (keeps StubRenderer definition local to this TU)
std::unique_ptr<Renderer> CreateStubRenderer()
{
	return std::make_unique<StubRenderer>();
}
