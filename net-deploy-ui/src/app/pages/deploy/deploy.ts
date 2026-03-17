import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TranslateModule } from '@ngx-translate/core';
import { ServicesMonitorService } from '../../services/services-monitor.service';
import { DeployService } from '../../services/deploy.service';
import { SettingsService } from '../../services/settings.service';
import { ServiceStatus, DeployLogEntry, VpsSettings } from '../../models/api-models';

@Component({
  selector: 'app-deploy',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslateModule],
  templateUrl: './deploy.html',
  styleUrl: './deploy.less'
})
export class DeployComponent implements OnInit {
  private servicesSvc = inject(ServicesMonitorService);
  private deploySvc = inject(DeployService);
  private settingsSvc = inject(SettingsService);

  services = signal<ServiceStatus[]>([]);
  environments = signal<VpsSettings[]>([]);
  selectedEnvironmentId = signal<string | null>(null);
  selectedServiceIds: Set<string> = new Set();
  serviceBranches: Record<string, string> = {}; // Stores custom branch per service

  loading = signal<boolean>(true);
  deploying = signal<boolean>(false);
  logs = signal<DeployLogEntry[]>([]);

  ngOnInit() {
    this.servicesSvc.getAll().subscribe({
      next: (data) => {
        this.services.set(data);
        // Initialize default branch for each service
        data.forEach(s => {
          if (s.id) this.serviceBranches[s.id] = s.branch || 'main';
        });
        this.loading.set(false);
      }
    });

    this.settingsSvc.getSettings().subscribe(data => {
      this.environments.set(data.vpsEnvironments || []);
      if (data.vpsEnvironments?.length > 0) {
        this.selectedEnvironmentId.set(data.vpsEnvironments[0].id || null);
      }
    });
  }

  setServiceBranch(id: string, branch: string) {
    this.serviceBranches[id] = branch;
  }

  toggleService(id: string) {
    if (this.selectedServiceIds.has(id)) {
      this.selectedServiceIds.delete(id);
    } else {
      this.selectedServiceIds.add(id);
    }
  }

  selectAll() {
    this.services().forEach(s => {
      if (s.id) this.selectedServiceIds.add(s.id);
    });
  }

  clearSelection() {
    this.selectedServiceIds.clear();
  }

  startDeploy() {
    if (this.selectedServiceIds.size === 0) return;

    this.deploying.set(true);
    this.logs.set([]);

    const deploymentConfigs = Array.from(this.selectedServiceIds).map(id => ({
      serviceId: id,
      branch: this.serviceBranches[id] || 'main'
    }));

    this.deploySvc.deploy(deploymentConfigs, this.selectedEnvironmentId()).subscribe({
      next: (entry) => {
        this.logs.update(prev => [...prev, entry]);
        this.scrollToBottom();
      },
      complete: () => {
        this.deploying.set(false);
        // Reload services to update status/last deployed date
        this.loading.set(true);
        this.servicesSvc.getAll().subscribe(data => {
          this.services.set(data);
          this.loading.set(false);
        });
      },
      error: (err) => {
        this.deploying.set(false);
        console.error('Deploy error', err);
        this.logs.update(prev => [...prev, {
          sessionId: 'client',
          level: 'ERROR',
          message: 'Connection to server failed or dropped randomly.',
          timestamp: new Date().toISOString()
        }]);
      }
    });
  }

  getLogClass(level: string): string {
    return `log-${level.toLowerCase()}`;
  }

  private scrollToBottom() {
    setTimeout(() => {
      const el = document.getElementById('logContainer');
      if (el) el.scrollTop = el.scrollHeight;
    }, 50);
  }
}
