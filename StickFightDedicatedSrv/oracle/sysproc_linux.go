//go:build linux

package oracle

import "syscall"

// sysProcAttrDetached returns SysProcAttr that puts the child in a new process
// group so SIGINT to the parent Go process doesn't auto-propagate and so
// Kill() can target the whole group.
func sysProcAttrDetached() *syscall.SysProcAttr {
	return &syscall.SysProcAttr{Setpgid: true}
}

// killGroup sends SIGTERM to the process group led by pid AND to any
// StickFight.exe process whose cmdline mentions our log path (Proton
// wineserver detaches the actual game from our session, so the process group
// approach alone misses it).
func killGroup(pid int) {
	_ = syscall.Kill(-pid, syscall.SIGTERM)
}

func killGroupForce(pid int) {
	_ = syscall.Kill(-pid, syscall.SIGKILL)
}
