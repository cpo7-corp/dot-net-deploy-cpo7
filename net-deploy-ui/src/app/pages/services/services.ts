import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TranslateModule } from '@ngx-translate/core';
import { ServicesMonitorService } from '../../services/services-monitor.service';
import { EnvConfigsService } from '../../services/env-configs.service';
import { ServiceDefinition, ServiceStatus, EnvConfigSet } from '../../models/api-models';

@Component({
  selector: 'app-services',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslateModule],
  templateUrl: './services.html',
  styleUrl: './services.less'
})
export class ServicesComponent implements OnInit {
  private servicesSvc = inject(ServicesMonitorService);
  private envConfigsSvc = inject(EnvConfigsService);

  services = signal<ServiceStatus[]>([]);
  envConfigs = signal<EnvConfigSet[]>([]);
  loading = signal<boolean>(true);
  newService: Partial<ServiceDefinition> = this.resetNewService();
  
  selectedService: ServiceStatus | null = null;
  isModalOpen = false;
  isAddModalOpen = false;

  ngOnInit() {
    this.loadData();
    this.loadEnvConfigs();
  }

  loadEnvConfigs() {
    this.envConfigsSvc.getAll().then(data => this.envConfigs.set(data));
  }

  loadData() {
    this.loading.set(true);
    console.log('Fetching services...');
    this.servicesSvc.getAll().subscribe({
      next: (data) => {
        console.log('Services received:', data);
        this.services.set(data.sort((a, b) => a.name.localeCompare(b.name)));
        this.loading.set(false);
      },
      error: (err) => {
        console.error('Error fetching services:', err);
        this.loading.set(false);
      }
    });
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
    this.selectedService = { ...service };
    this.isModalOpen = true;
  }

  closeModal() {
    this.isModalOpen = false;
    this.selectedService = null;
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

  private resetNewService(): Partial<ServiceDefinition> {
    return { name: '', repoUrl: '', projectPath: '', iisSiteName: '', serviceType: 'WebApi', deployTargetPath: '', branch: '', compileSingleFile: false, heartbeatUrl: '', envConfigSetIds: [] };
  }

  toggleEnvConfig(service: Partial<ServiceDefinition>, configId: string) {
    if (!service.envConfigSetIds) {
      service.envConfigSetIds = [];
    }
    const idx = service.envConfigSetIds.indexOf(configId);
    if (idx >= 0) {
      service.envConfigSetIds.splice(idx, 1);
    } else {
      service.envConfigSetIds.push(configId);
    }
  }
}
