## 📀 Packer Template Build & Autoinstall Manual

This module leverages **Packer** and Ubuntu’s native **Autoinstall (Subiquity)** engine to automate the generation of an identical, immutable base OS image template (**ID 777**) hosted on your Proxmox pool.

---

### 1. The Provisioning Lifecycle Mechanics

The pipeline is completely automated through the local `Makefile`. When executing a build, Packer orchestrates the following operations sequentially:

1. **Local HTTP Server Initiation:** Packer boots a temporary HTTP engine bound to `http_bind_address_ip` on port `8688` to expose your `http/user-data` configurations.
2. **ISO Instantiation:** Provisions a vanilla Ubuntu 24.04 VM, attaches the installer ISO, and handles GRUB boot directives over VNC to catch the hosted automated cloud-config blueprint.
3. **Unattended Automated Installation:** Subiquity reads the recipe, establishes partitions, seeds your public SSH key (`gman@fedora`), configures passwordless `sudo`, and bakes down the `qemu-guest-agent`.
4. **Shell Ingestion Pipeline:** \* **Stage 1 (K3s Offline Prep):** Pulls the installer payload script from `get.k3s.io` and pre-runs it using `INSTALL_K3S_SKIP_START=true`. This caches binary hooks and setups paths without caching unique runtime configurations.

- **Stage 2 (Sanitization):** Wipes machine signatures (`/etc/machine-id`), cleans APT cache pools, clears command strings, and clears transient cloud-init data states.

5. **Conversion to Template:** Shuts down the sanitized VM and hardlocks it as a Proxmox cluster deployment template.

---

### 2. Automation Targets (`Makefile`)

Execute these targets from the root folder directory to run validation or initialization steps:

```bash
# Fetch and compile the Proxmox builder plugin dependencies
make init

# Run structural HCL syntax and lint checks using the variables file
make validate

# Standard production build pipeline execution run
make build

# Debug mode execution: steps pause iteratively and stream verbosely (PACKER_LOG=1)
make build-debug

# Clean down local cache workspace structures
make clean

```

---

### 3. Autoinstall Configuration Matrix (`user-data`)

The Subiquity installer engine isolates configuration blocks cleanly:

- **Identity Target:** Spins up the baseline user space `gman` with a pre-hashed secure user shadow string footprint.
- **Storage Allocation:** Leverages a `direct` storage mapping layout to avoid arbitrary LVM volume grouping over the raw `20G` disk allocation.
- **Late Command Execution Blocks:** ```yaml
  late-commands:

# Forces cloud-init to evaluate cleanly on the first boot after being cloned

- curtin in-target -- cloud-init clean

# Generates a drop-in file to grant explicit passwordless sudo capabilities to gman

- echo "gman ALL=(ALL) NOPASSWD:ALL" > /target/etc/sudoers.d/gman
- chmod 440 /target/etc/sudoers.d/gman

---

---

---

Here is a comprehensive breakdown of the `boot_command` sequence, designed to be inserted directly into your `README.md`. It explains the precise timing, keystrokes, and kernel parameters required to bypass the manual Ubuntu installer interface.

---

### 🕹️ Deep Dive: The Packer `boot_command` Sequence

> [!IMPORTANT]
> Extremely sensitive section. Pay special attention.

The `boot_command` property simulates a human physically typing on a keyboard connected to the virtual machine's console during its initial POST screen. Because the Ubuntu live-server installer boots into a graphical menu by default, this automated macro intercepts the bootloader to inject your unattended autoinstall configuration.

```hcl
  boot_command = [
    "<esc><wait3>",
    "c<wait3>",
    "linux /casper/vmlinuz autoinstall <wait>",
    "\"ds=nocloud;s=http://{{ .HTTPIP }}:{{ .HTTPPort }}/\" <wait3>",
    "---<enter><wait3>",
    "initrd /casper/initrd<enter><wait3>",
    "boot<enter>"
  ]

```

#### Line-by-Line Execution Breakdown

1. **`"<esc><wait3>"`**

- **Action:** Presses the `Escape` key and pauses for 3 seconds.
- **Purpose:** Interrupts the initial default GRUB boot menu splash screen before it times out and automatically boots into the normal interactive installation wizard.

2. **`"c<wait3>"`**

- **Action:** Presses the letter `c` and pauses for 3 seconds.
- **Purpose:** Instructs GRUB to switch into its native command-line interface prompt (`grub>`), giving us an environment where we can manually type boot paths.

3. **`"linux /casper/vmlinuz autoinstall <wait>"`**

- **Action:** Types the command to load the Linux kernel payload (`vmlinuz`) located inside the ISO image's `casper` directory. It appends the `autoinstall` flag.
- **Purpose:** Tells the kernel that this boot sequence should look for an unattended Subiquity configuration rather than spawning human-facing setup questions.

4. **`"\"ds=nocloud;s=http://{{ .HTTPIP }}:{{ .HTTPPort }}/\" <wait3>"`**

- **Action:** Types the data-source parameter string, escaping the quotes. Packer automatically substitutes `{{ .HTTPIP }}` and `{{ .HTTPPort }}` with its local temporary web server details (e.g., `http://192.168.50.55:8688/`).
- **Purpose:** Directs the kernel's `cloud-init` subsystem to search for structural `user-data` and `meta-data` instruction files hosted remotely on your network instead of searching local storage arrays.

5. **`"---<enter><wait3>"`**

- **Action:** Types three hyphens, presses `Enter`, and waits 3 seconds.
- **Purpose:** The `---` sequence separates standard kernel options from arguments passed straight to the installer environment. Pressing `Enter` commits the full `linux` entry string to GRUB memory.

6. **`"initrd /casper/initrd<enter><wait3>"`**

- **Action:** Types the path to the initial RAM disk (`initrd`) file structure, presses `Enter`, and waits 3 seconds.
- **Purpose:** Loads the temporary filesystem drivers and utilities needed by the kernel into memory to execute the core installation phase safely.

7. **`"boot<enter>"`**

- **Action:** Types `boot` and hits `Enter`.
- **Purpose:** Fires the execution signal. The VM spins up using the custom kernel arguments, fetches your configuration template over the local HTTP bridge, and transitions completely into silent autoinstall mode.

#### ⚠️ Essential Engineering Notes

- **The Importance of `<wait3>`:** Virtualization environments experience slight disk/CPU scheduling latencies while starting up on Proxmox. The embedded `<wait3>` directives act as defensive buffers, preventing Packer from typing commands faster than the VM's virtual keyboard buffer can receive them.
- **Network Availability:** Because the kernel pulls the autoinstall files from an HTTP link _during_ this step, the VM's hardware abstraction layer must instantly receive a functional IP address from your local network gateway bridge via DHCP the second the network device turns on.
