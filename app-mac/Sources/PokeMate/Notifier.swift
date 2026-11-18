import Foundation
import UserNotifications

/// Mac 알림. 알림을 누르면 onOpen 이 불린다. .app 으로 묶어 실행할 때만 동작한다(번들 ID 필요).
final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    private let onOpen: ([AnyHashable: Any]) -> Void
    private var center: UNUserNotificationCenter? {
        Bundle.main.bundleIdentifier == nil ? nil : UNUserNotificationCenter.current()
    }

    init(onOpen: @escaping ([AnyHashable: Any]) -> Void) {
        self.onOpen = onOpen
        super.init()
        center?.delegate = self
        center?.requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }

    func send(title: String, body: String, id: String? = nil, info: [String: String] = [:]) {
        guard let center else {
            print("[알림] \(title)\n\(body)")
            return
        }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.sound = .default
        content.userInfo = info
        center.add(UNNotificationRequest(identifier: id ?? UUID().uuidString, content: content, trigger: nil))
    }

    func remove(id: String) {
        center?.removeDeliveredNotifications(withIdentifiers: [id])
        center?.removePendingNotificationRequests(withIdentifiers: [id])
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                withCompletionHandler completionHandler: @escaping () -> Void) {
        let info = response.notification.request.content.userInfo
        DispatchQueue.main.async { self.onOpen(info) }
        completionHandler()
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .sound])
    }
}
