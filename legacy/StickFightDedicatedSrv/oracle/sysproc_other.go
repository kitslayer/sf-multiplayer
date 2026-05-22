//go:build !linux

package oracle

import "syscall"

// sysProcAttrDetached is a no-op on non-Linux. Headless oracle launch is
// Linux-only for now (depends on Proton + Goldberg layout).
func sysProcAttrDetached() *syscall.SysProcAttr { return nil }

// killGroup signals an entire process group; non-Linux stub.
func killGroup(pid int)      {}
func killGroupForce(pid int) {}
