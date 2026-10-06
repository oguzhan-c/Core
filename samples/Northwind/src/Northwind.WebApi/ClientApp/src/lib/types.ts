// Backend DTO'larının TypeScript karşılıkları (JSON camelCase, enum'lar metin).

export interface Paginate<T> {
  from: number;
  index: number;
  size: number;
  count: number;
  pages: number;
  items: T[];
  hasPrevious: boolean;
  hasNext: boolean;
}

export interface Address {
  street: string;
  city: string;
  region?: string | null;
  postalCode?: string | null;
  country: string;
}

// ---------------------------------------------------------------- kimlik

export type Role = "Admin" | "Sales" | "Warehouse" | "Customer";

export interface UserProfile {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  emailConfirmed: boolean;
  tenantId: string;
  tenant?: string | null;
  tenantName?: string | null;
  roles: Role[];
}

// ---------------------------------------------------------------- hesap güvenliği

/** Girişte şifreden sonra istenen ikinci adım. */
export type TwoFactorMethod = "None" | "Email" | "Otp";

export interface TwoFactorPrompt {
  method: TwoFactorMethod;
  /** E-posta yönteminde maskelenmiş adres (me*****@gmail.com). */
  destination?: string | null;
}

export interface LoginResponse {
  user: UserProfile | null;
  twoFactor: TwoFactorPrompt | null;
}

export interface AuthFeatures {
  passkeys: boolean;
}

export interface PasskeyInfo {
  id: string;
  name: string;
  createdAt: string;
  lastUsedAt?: string | null;
  isBackedUp: boolean;
}

export interface AccountSecurity {
  email: string;
  emailConfirmed: boolean;
  twoFactor: TwoFactorMethod;
  passkeysEnabled: boolean;
  passkeys: PasskeyInfo[];
}

export interface OtpSetup {
  secret: string;
  provisioningUri: string;
}

export interface RegisterResult {
  email: string;
  codeExpiresAt: string;
}

// ---------------------------------------------------------------- mağaza

export interface StoreTenant {
  identifier: string;
  name: string;
}

export interface StoreCategory {
  id: string;
  name: string;
  description?: string | null;
  productCount: number;
}

export interface StoreProduct {
  id: string;
  name: string;
  categoryId?: string | null;
  categoryName?: string | null;
  supplierName?: string | null;
  quantityPerUnit?: string | null;
  unitPrice: number;
  unitsInStock: number;
}

export type StoreProductSort = "Name" | "PriceAsc" | "PriceDesc";

export interface PlaceOrderResult {
  id: string;
  number: number;
  total: number;
}

// ---------------------------------------------------------------- katalog (yönetim)

export interface Category {
  id: string;
  name: string;
  description?: string | null;
  productCount: number;
}

export interface Product {
  id: string;
  name: string;
  categoryId?: string | null;
  supplierId?: string | null;
  quantityPerUnit?: string | null;
  unitPrice: number;
  unitsInStock: number;
  unitsOnOrder: number;
  reorderLevel: number;
  isDiscontinued: boolean;
  needsReorder: boolean;
}

export interface ProductListItem {
  id: string;
  name: string;
  categoryName?: string | null;
  supplierName?: string | null;
  quantityPerUnit?: string | null;
  unitPrice: number;
  unitsInStock: number;
  isDiscontinued: boolean;
}

export interface Supplier {
  id: string;
  companyName: string;
  contactName?: string | null;
  contactTitle?: string | null;
  city?: string | null;
  country?: string | null;
  phone?: string | null;
  homePage?: string | null;
}

export interface Shipper {
  id: string;
  companyName: string;
  phone?: string | null;
}

export interface Employee {
  id: string;
  firstName: string;
  lastName: string;
  title?: string | null;
  hireDate?: string | null;
  city?: string | null;
  country?: string | null;
  managerId?: string | null;
  managerName?: string | null;
  territories: string[];
}

// ---------------------------------------------------------------- satış

export interface Customer {
  id: string;
  code: string;
  companyName: string;
  contactName?: string | null;
  contactTitle?: string | null;
  address?: Address | null;
  phone?: string | null;
  fax?: string | null;
}

export interface CustomerListItem {
  id: string;
  code: string;
  companyName: string;
  contactName?: string | null;
  city?: string | null;
  country?: string | null;
  phone?: string | null;
}

export type OrderStatus = "Placed" | "Shipped" | "Cancelled";

export interface OrderListItem {
  id: string;
  number: number;
  customerId: string;
  customerName: string;
  status: OrderStatus;
  orderedAt: string;
  shippedAt?: string | null;
  total: number;
}

export interface OrderLine {
  productId: string;
  productName: string;
  unitPrice: number;
  quantity: number;
  discount: number;
  lineTotal: number;
}

export interface Order {
  id: string;
  number: number;
  status: OrderStatus;
  customerId: string;
  customerName: string;
  employeeId?: string | null;
  employeeName?: string | null;
  orderedAt: string;
  requiredDate?: string | null;
  shippedAt?: string | null;
  shipperId?: string | null;
  shipperName?: string | null;
  shipName: string;
  shipAddress: Address;
  freight: number;
  subtotal: number;
  total: number;
  lines: OrderLine[];
}

export interface SalesByCategory {
  category: string;
  orders: number;
  quantity: number;
  revenue: number;
}

export interface TopCustomer {
  customerId: string;
  companyName: string;
  country?: string | null;
  orders: number;
  revenue: number;
}

// ---------------------------------------------------------------- yönetim

export interface Dashboard {
  productCount: number;
  customerCount: number;
  openOrderCount: number;
  productsToReorder: number;
  totalRevenue: number;
  recentOrders: OrderListItem[];
}

export type AuditAction = "Created" | "Updated" | "Deleted";

export interface AuditLog {
  id: string;
  entityType: string;
  entityId: string;
  action: AuditAction;
  changes?: string | null;
  userId?: string | null;
  timestamp: string;
  traceId?: string | null;
}

export type OutboxStatus = "Pending" | "Processed" | "Failed";

export interface OutboxMessage {
  id: string;
  eventId: string;
  eventType: string;
  payload: string;
  occurredAt: string;
  processedAt?: string | null;
  attempts: number;
  lastError?: string | null;
  status: OutboxStatus;
}

export interface UserListItem {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  emailConfirmed: boolean;
  lockoutEnd?: string | null;
  createdAt: string;
  roles: Role[];
}

export interface Mail {
  id: string;
  date: string;
  from?: string | null;
  to: string;
  subject: string;
  text?: string | null;
  html?: string | null;
}

// ---------------------------------------------------------------- dinamik sorgu

export interface Filter {
  field?: string | null;
  operator?: string | null;
  value?: string | null;
  logic?: "and" | "or" | null;
  caseSensitive?: boolean;
  filters?: Filter[] | null;
}

export interface Sort {
  field: string;
  dir: "asc" | "desc";
}

export interface DynamicQuery {
  sort?: Sort[] | null;
  filter?: Filter | null;
}

export interface SearchExamples {
  operators: string[];
  notes: string[];
  examples: { title: string; endpoint: string; body: DynamicQuery }[];
}
