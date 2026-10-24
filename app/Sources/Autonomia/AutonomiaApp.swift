import SwiftUI

let sharedModel = AppModel()

struct AutonomiaApp: App {
    var body: some Scene {
        MenuBarExtra {
            PanelView(model: sharedModel)
        } label: {
            StatusLabel(model: sharedModel)
        }
        .menuBarExtraStyle(.window)
    }
}
