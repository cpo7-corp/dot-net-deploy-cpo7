import { Component, inject, OnInit, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { TranslateModule } from '@ngx-translate/core';
import { CdkDragDrop, moveItemInArray, CdkDropList, CdkDrag, CdkDragHandle } from '@angular/cdk/drag-drop';
import { ServicesMonitorService } from '../../services/services-monitor.service';
import { SettingsService } from '../../services/settings.service';
import { EnvConfigsService } from '../../services/env-configs.service';
import { DeployService } from '../../services/deploy.service';
import { ServiceStatus, ServiceDefinition, ServiceEnvironmentConfig, VpsSettings, EnvConfigSet } from '../../models/api-models';

@Component({
  selector: 'app-services',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslateModule, CdkDropList, CdkDrag, CdkDragHandle],
  templateUrl: './services.html',
  styleUrl: './services.less'
})
export class ServicesComponent implements OnInit {
  private servicesSvc = inject(ServicesMonitorService);
  private settingsSvc = inject(SettingsService);
  private configSvc = inject(EnvConfigsService);
  private deploySvc = inject(DeployService);
  private http = inject(HttpClient);

  services = signal<ServiceStatus[]>([]);
  loading = signal<boolean>(true);
  allConfigSets = signal<EnvConfigSet[]>([]);
  environments = signal<VpsSettings[]>([]);

  targetEnvId: string | null = null;
  activeActionServiceId: string | null = null;
  isModalOpen = false;

  isAddModalOpen = false;
  isConfigLookupOpen = false;
  configSearchQuery = '';

  selectedService: ServiceStatus | null = null;
  newService: Partial<ServiceDefinition> = this.resetNewService();
  activeEnvTab: string | null = 'general';

  filteredConfigSets = computed(() => {
    const q = this.configSearchQuery.toLowerCase();
    return this.allConfigSets().filter(s => s.name.toLowerCase().includes(q));
  });

  ngOnInit() {
    this.loadData();
    this.loadConfigSets();
    this.loadEnvironments();
  }

  loadData() {
    this.loading.set(true);
    this.servicesSvc.getAll().subscribe({
      next: (data) => {
        this.services.set(data);
        this.loading.set(false);
        this.checkHeartbeats();
      },
      error: () => this.loading.set(false)
    });
  }

  checkHeartbeats() {
    this.services().forEach(s => {
      // For SPA services (Angular, React), we also check Heartbeat in the UI
      if (s.serviceType === 'Angular' || s.serviceType === 'React') {
        const env = s.environments?.find(e => e.environmentId === this.targetEnvId);
        if (env?.heartbeatUrl) {
          s.isChecking = true;
          s.hbStatus = 'Checking';
          this.services.update(list => [...list]);

          // Using native fetch with 'no-cors' mode to avoid CORS blocks.
          // In 'no-cors' mode, we can't read the response body, but we know if the server responded.
          fetch(env.heartbeatUrl, { mode: 'no-cors', cache: 'no-cache' })
            .then(() => {
              s.hbStatus = 'Running';
              s.isChecking = false;
              this.services.update(list => [...list]);
            })
            .catch(() => {
              s.hbStatus = 'Stopped';
              s.isChecking = false;
              this.services.update(list => [...list]);
            });
        }
      }
    });
  }

  loadConfigSets() {
    this.configSvc.getAll().subscribe(sets => this.allConfigSets.set(sets));
  }

  loadEnvironments() {
    this.settingsSvc.getSettings().subscribe(s => {
      const envs = s.vpsEnvironments || [];
      this.environments.set(envs);
      
      const savedEnvId = localStorage.getItem('lastTargetEnvId');
      const exists = envs.some(e => e.id === savedEnvId);

      if (savedEnvId && exists) {
        this.targetEnvId = savedEnvId;
      } else if (envs.length > 0) {
        this.targetEnvId = envs[0].id || null;
      }
    });
  }

  onTargetEnvChange(id: string | null) {
    if (id) {
      localStorage.setItem('lastTargetEnvId', id);
      this.loadData();
    }
  }

  runAction(serviceId: string, action: string) {
    if (!this.targetEnvId || this.activeActionServiceId) return;
    this.activeActionServiceId = serviceId;

    this.deploySvc.serviceAction(serviceId, this.targetEnvId, action).subscribe({
      next: (log) => {
        // We could show these in a console component if we wanted
        console.log(`[Action: ${action}]`, log.message);
      },
      complete: () => {
        // Refresh statuses after action completes
        setTimeout(() => {
          this.loadData();
          this.activeActionServiceId = null;
        }, 1200);
      },
      error: (err) => {
        this.activeActionServiceId = null;
        alert('Action failed: ' + err.message);
      }
    });
  }


  allEnvironments(): VpsSettings[] {
    return this.environments();
  }

  openAddModal() {
    this.newService = this.resetNewService();
    this.isAddModalOpen = true;
  }

  closeAddModal() {
    this.isAddModalOpen = false;
  }

  addService() {
    if (!this.newService.name || !this.newService.repoUrl) return;
    this.servicesSvc.create(this.newService as ServiceDefinition).subscribe({
      next: () => {
        this.closeAddModal();
        this.loadData();
      }
    });
  }

  openEditModal(service: ServiceStatus) {
    this.selectedService = JSON.parse(JSON.stringify(service)); // Deep copy
    if (!this.selectedService!.environments) this.selectedService!.environments = [];

    // Ensure all environments have a config set up
    this.allEnvironments().forEach(env => {
      let cfg = this.selectedService!.environments.find(e => e.environmentId === env.id);
      if (env.id && !cfg) {
        cfg = {
          environmentId: env.id,
          deployTargetPath: '',
          heartbeatUrl: '',
          defaultBranch: 'main',
          configSetIds: []
        };
        this.selectedService!.environments.push(cfg);
      }
      if (cfg && !cfg.configSetIds) cfg.configSetIds = [];
    });

    this.activeEnvTab = 'general';
    this.isModalOpen = true;
  }

  closeModal() {
    this.isModalOpen = false;
    this.selectedService = null;
    this.activeEnvTab = 'general';
  }

  updateService() {
    if (!this.selectedService || !this.selectedService.id) return;
    this.servicesSvc.update(this.selectedService.id, this.selectedService).subscribe({
      next: () => {
        this.closeModal();
        this.loadData();
      }
    });
  }

  deleteService(id: string) {
    if (!confirm('Are you sure you want to delete this service definition?')) return;
    this.servicesSvc.delete(id).subscribe({
      next: () => {
        this.closeModal();
        this.loadData();
      }
    });
  }

  onDrop(event: CdkDragDrop<ServiceStatus[]>) {
    const servicesArray = [...this.services()];
    moveItemInArray(servicesArray, event.previousIndex, event.currentIndex);
    this.services.set(servicesArray);

    const ids = servicesArray.map(s => s.id!).filter(id => !!id);
    this.servicesSvc.reorder(ids).subscribe();
  }

  private resetNewService(): Partial<ServiceDefinition> {
    return { 
      name: '', 
      repoUrl: '', 
      projectPath: '', 
      iisSiteName: '', 
      serviceType: 'WebApi', 
      compileSingleFile: false,
      environments: [] 
    };
  }

  getEnvConfig(envId: string): ServiceEnvironmentConfig {
    if (!this.selectedService || !this.selectedService.environments) return {} as any;
    const config = this.selectedService.environments.find(e => e.environmentId === envId);
    return config || ({} as any);
  }

  toggleConfigSet(envId: string, setId: string) {
    const cfg = this.getEnvConfig(envId);
    if (!cfg.configSetIds) cfg.configSetIds = [];
    
    const index = cfg.configSetIds.indexOf(setId);
    if (index > -1) cfg.configSetIds.splice(index, 1);
    else cfg.configSetIds.push(setId);
  }

  isConfigSetSelected(envId: string, setId: string): boolean {
    const cfg = this.getEnvConfig(envId);
    return cfg.configSetIds?.includes(setId) || false;
  }

  getSelectedConfigSets(envId: string): EnvConfigSet[] {
    const ids = this.getEnvConfig(envId).configSetIds || [];
    return this.allConfigSets().filter(s => ids.includes(s.id!));
  }

  openConfigLookup() {
    this.configSearchQuery = '';
    this.isConfigLookupOpen = true;
  }

  closeConfigLookup() {
    this.isConfigLookupOpen = false;
  }
}
